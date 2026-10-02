using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Infrastructure.Documentation;
using UnityDocsRag.Infrastructure.Embeddings;
using UnityDocsRag.Infrastructure.Preprocessing;
using UnityDocsRag.Infrastructure.Storage;

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
    if (args.Length > 2 || (command is not "grab" and not "process" and not "index" && args.Length != 1))
    {
        throw new ArgumentException("Usage: [grab [config]] | [process [config]] | [index [config]] | [legacy-grab-config]");
    }

    if (command == "index")
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
catch (Exception exception)
{
    logger.LogError("Command failed ({ExceptionType}). Usage: [grab [config]] | [process [config]] | [index [config]] | [legacy-grab-config]",
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
