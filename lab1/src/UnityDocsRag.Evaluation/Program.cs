using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnityDocsRag.Evaluation;
using UnityDocsRag.Infrastructure.Embeddings;
using UnityDocsRag.Infrastructure.Generation;
using UnityDocsRag.Infrastructure.Query;
using UnityDocsRag.Infrastructure.Retrieval;
using UnityDocsRag.Infrastructure.Reranking;
using UnityDocsRag.Infrastructure.Storage;
using UnityDocsRag.Ingestion;

var isAskEvaluation = args.Length == 4 && args[0] == "evaluate-ask";
var isRetrievalEvaluation = args.Length == 3 && args[0] != "evaluate-ask";
if (!isAskEvaluation && !isRetrievalEvaluation)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/UnityDocsRag.Evaluation -- <questions.json> <search.json> <output.json> | evaluate-ask <questions.json> <ask.json> <output.json>");
    return 2;
}

var (datasetPath, configPath, outputPath) = isAskEvaluation
    ? (args[1], args[2], args[3])
    : (args[0], args[1], args[2]);
if (File.Exists(outputPath))
{
    Console.Error.WriteLine("Output file already exists; it will not be overwritten.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var dataset = EvaluationDataset.Load(datasetPath);
    if (isAskEvaluation)
    {
        await using var askConfigStream = File.OpenRead(configPath);
        var askOptions = await JsonSerializer.DeserializeAsync<AskRunOptions>(askConfigStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellation.Token)
            ?? throw new InvalidDataException("Ask configuration is empty or invalid.");
        askOptions.Validate();

        var askPostgresOptions = new PostgresVectorStoreOptions
        {
            ConnectionString = askOptions.GetPostgresConnectionString(),
            CommandTimeoutSeconds = askOptions.PostgresCommandTimeoutSeconds
        };
        await using var askDataSource = askPostgresOptions.CreateDataSource();
        using var askHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var profile = askOptions.CreateEmbeddingProfile();
        var askEmbeddingProvider = new OllamaEmbeddingProvider(askHttpClient,
            askOptions.CreateOllamaEmbeddingOptions());
        var askRetriever = new PostgresVectorRetriever(askDataSource,
            new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = askOptions.PostgresCommandTimeoutSeconds });
        var askSearch = new SemanticSearchService(askEmbeddingProvider, askRetriever);
        var reranker = new OllamaReranker(askHttpClient, askOptions.CreateOllamaRerankerOptions());
        var generator = new OllamaAnswerGenerator(askHttpClient, askOptions.CreateOllamaGenerationOptions());
        var queryService = new RagQueryService(
            new RussianEnglishLanguageDetector(), askSearch, reranker, generator,
            NullLogger<RagQueryService>.Instance, profile,
            askOptions.TopK, askOptions.DomainSimilarityThreshold,
            askOptions.EvidenceSimilarityThreshold, askOptions.MaxEvidenceChunks);
        var askReport = await new AskEvaluationRunner(queryService).RunResumableAsync(
            dataset, datasetPath, configPath, outputPath, Console.WriteLine, cancellation.Token);
        await AskEvaluationReportWriter.WriteNewAsync(askReport, outputPath, cancellation.Token);
        return 0;
    }

    await using var configStream = File.OpenRead(configPath);
    var options = await JsonSerializer.DeserializeAsync<SearchRunOptions>(configStream,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellation.Token)
        ?? throw new InvalidDataException("Search configuration is empty or invalid.");
    options.Validate();

    var postgresOptions = new PostgresVectorStoreOptions
    {
        ConnectionString = options.GetPostgresConnectionString(),
        CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds
    };
    await using var dataSource = postgresOptions.CreateDataSource();
    using var httpClient = new HttpClient();
    var embeddingProvider = new OllamaEmbeddingProvider(httpClient, options.CreateOllamaOptions());
    var retriever = new PostgresVectorRetriever(dataSource,
        new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds });
    var search = new SemanticSearchService(embeddingProvider, retriever);
    var report = await new RetrievalEvaluationRunner(search).RunAsync(
        dataset, options.CreateEmbeddingProfile(), cancellation.Token);

    // Serialize before CreateNew so serialization failures cannot leave an empty output file.
    var outputBytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
    await using var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await outputStream.WriteAsync(outputBytes, cancellation.Token);
    Console.WriteLine($"Retrieval evaluation saved: {report.Summary.AnsweredQuestionCount} answered questions.");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine(isAskEvaluation ? "Ask evaluation cancelled." : "Retrieval evaluation cancelled.");
    return 130;
}
catch (IOException) when (File.Exists(outputPath))
{
    Console.Error.WriteLine("Output file already exists; it will not be overwritten.");
    return 2;
}
catch (Exception exception)
{
    // Do not print exception messages: provider or database errors might contain sensitive input.
    Console.Error.WriteLine($"{(isAskEvaluation ? "Ask" : "Retrieval")} evaluation failed ({exception.GetType().Name}).");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
