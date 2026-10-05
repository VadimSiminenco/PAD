using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Infrastructure.Documentation;
using UnityDocsRag.Infrastructure.Embeddings;
using UnityDocsRag.Infrastructure.Generation;
using UnityDocsRag.Infrastructure.Preprocessing;
using UnityDocsRag.Infrastructure.Query;
using UnityDocsRag.Infrastructure.Retrieval;
using UnityDocsRag.Infrastructure.Reranking;
using UnityDocsRag.Infrastructure.Storage;
using UnityDocsRag.Ingestion;

using var cancellationSource = new CancellationTokenSource();
using var loggerFactory = LoggerFactory.Create(logging =>
    logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("UnityDocsRag.Ingestion");
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var command = args.Length == 0 ? "grab" : args[0];
    var validArguments = command is "search" or "ask"
        ? args.Length is 2 or 3
        : args.Length <= 2 && (command is "grab" or "process" or "index" || args.Length == 1);
    if (!validArguments)
    {
        throw new ArgumentException("Usage: [grab [config]] | [process [config]] | [index [config]] | search \"<question>\" [config] | ask \"<question>\" [config] | [legacy-grab-config]");
    }

    if (command == "ask")
    {
        var question = args[1];
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Ask question must not be empty.");
        var configPath = args.Length == 3 ? args[2] : Path.Combine("configs", "ask.json");
        var options = await ReadOptionsAsync<AskRunOptions>(configPath, cancellationSource.Token);
        options.Validate();
        var connectionString = options.GetPostgresConnectionString();
        var postgresOptions = new PostgresVectorStoreOptions
        {
            ConnectionString = connectionString,
            CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds
        };
        await using var dataSource = postgresOptions.CreateDataSource();
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var profile = options.CreateEmbeddingProfile();
        var embeddingProvider = new OllamaEmbeddingProvider(httpClient, options.CreateOllamaEmbeddingOptions());
        var retriever = new PostgresVectorRetriever(dataSource,
            new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds });
        var semanticSearch = new SemanticSearchService(embeddingProvider, retriever);
        var reranker = new OllamaReranker(httpClient, options.CreateOllamaRerankerOptions());
        var answerGenerator = new OllamaAnswerGenerator(httpClient, options.CreateOllamaGenerationOptions());
        var queryService = new RagQueryService(
            new RussianEnglishLanguageDetector(), semanticSearch, reranker, answerGenerator,
            loggerFactory.CreateLogger<RagQueryService>(), profile,
            options.TopK, options.DomainSimilarityThreshold, options.EvidenceSimilarityThreshold, options.MaxEvidenceChunks);
        var runner = new AskRunner(queryService, loggerFactory.CreateLogger<AskRunner>());
        await runner.RunAsync(question, cancellationSource.Token);
    }
    else if (command == "search")
    {
        var question = args[1];
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Search question must not be empty.");
        var configPath = args.Length == 3 ? args[2] : Path.Combine("configs", "search.json");
        var options = await ReadOptionsAsync<UnityDocsRag.Ingestion.SearchRunOptions>(configPath, cancellationSource.Token);
        options.Validate();
        var connectionString = options.GetPostgresConnectionString();
        var postgresOptions = new PostgresVectorStoreOptions
        {
            ConnectionString = connectionString,
            CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds
        };
        await using var dataSource = postgresOptions.CreateDataSource();
        using var httpClient = new HttpClient();
        var embeddingProvider = new OllamaEmbeddingProvider(httpClient, options.CreateOllamaOptions());
        var retriever = new PostgresVectorRetriever(dataSource,
            new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds });
        var service = new SemanticSearchService(embeddingProvider, retriever);
        var runner = new UnityDocsRag.Ingestion.SearchRunner(service, options.CreateEmbeddingProfile(),
            loggerFactory.CreateLogger<UnityDocsRag.Ingestion.SearchRunner>());
        await runner.RunAsync(question, options.TopK, options.SimilarityThreshold, cancellationSource.Token);
    }
    else if (command == "index")
    {
        var configPath = args.Length == 2 ? args[1] : Path.Combine("configs", "indexing.json");
        var options = await ReadOptionsAsync<UnityDocsRag.Ingestion.IndexingRunOptions>(configPath, cancellationSource.Token);
        options.Validate();
        var snapshot = await new ProcessingArtifactReader().ReadAsync(options.ProcessedDocumentsPath, options.ChunksPath,
            cancellationSource.Token);
        var connectionString = options.GetPostgresConnectionString();
        var postgresOptions = new PostgresVectorStoreOptions
        {
            ConnectionString = connectionString,
            CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds
        };
        await using var dataSource = postgresOptions.CreateDataSource();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(options.OllamaRequestTimeoutSeconds) };
        var embeddingProvider = new OllamaEmbeddingProvider(httpClient, options.CreateOllamaOptions());
        var vectorStore = new PostgresVectorStore(dataSource, postgresOptions);
        var runner = new UnityDocsRag.Ingestion.IndexingRunner(embeddingProvider, vectorStore,
            loggerFactory.CreateLogger<UnityDocsRag.Ingestion.IndexingRunner>());
        await runner.RunAsync(snapshot, options.CreateEmbeddingProfile(), cancellationSource.Token);
    }
    else if (command == "process")
    {
        var configPath = args.Length == 2 ? args[1] : Path.Combine("configs", "processing.json");
        var options = await ReadOptionsAsync<ProcessingRunOptions>(configPath, cancellationSource.Token);
        options.Validate();
        IDocumentSource source = new CachedUnityDocumentSource(options);
        var runner = new UnityDocsRag.Ingestion.ProcessingRunner(source, new UnityHtmlDocumentPreprocessor(),
            new HeadingAwareDocumentChunker(options.CreateChunkingOptions()), new FileProcessingArtifactStore(options),
            loggerFactory.CreateLogger<UnityDocsRag.Ingestion.ProcessingRunner>());
        await runner.RunAsync(cancellationSource.Token);
    }
    else
    {
        var configPath = command == "grab"
            ? args.Length == 2 ? args[1] : Path.Combine("configs", "ingestion.json")
            : args[0];
        var sourceOptions = await ReadOptionsAsync<UnityDocumentationSourceOptions>(configPath, cancellationSource.Token);
        sourceOptions.Validate();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(sourceOptions.RequestTimeoutSeconds) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(sourceOptions.UserAgent);
        var documentSource = new UnityScriptingApiDocumentSource(httpClient, sourceOptions,
            new UnityDocumentationLinkExtractor(), loggerFactory.CreateLogger<UnityScriptingApiDocumentSource>());
        var cache = new FileDocumentCache(sourceOptions);
        var runner = new UnityDocsRag.Ingestion.IngestionRunner(documentSource, cache,
            loggerFactory.CreateLogger<UnityDocsRag.Ingestion.IngestionRunner>());
        await runner.RunAsync(cancellationSource.Token);
    }
    return 0;
}
catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
{
    logger.LogInformation("Command cancelled by user");
    return 130;
}
catch (OllamaAnswerValidationException exception) when (args.Length > 0 && args[0] == "ask")
{
    logger.LogError("Ask generation failed ({ExceptionType}; ReasonCode={ReasonCode}; Attempt={AttemptNumber})",
        exception.GetType().Name, exception.ReasonCode, exception.AttemptNumber);
    return 1;
}
catch (Exception exception)
{
    logger.LogError("Command failed ({ExceptionType}). Usage: [grab [config]] | [process [config]] | [index [config]] | search \"<question>\" [config] | ask \"<question>\" [config] | [legacy-grab-config]",
        exception.GetType().Name);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task<T> ReadOptionsAsync<T>(string configPath, CancellationToken cancellationToken) where T : class
{
    await using var configStream = File.OpenRead(configPath);
    return await JsonSerializer.DeserializeAsync<T>(configStream,
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
           ?? throw new InvalidDataException($"Configuration file '{configPath}' is empty or invalid.");
}
