using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnityDocsRag.Infrastructure.Documentation;

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
    var configPath = args.Length > 0 ? args[0] : Path.Combine("configs", "ingestion.json");
    await using var configStream = File.OpenRead(configPath);
    var sourceOptions = await JsonSerializer.DeserializeAsync<UnityDocumentationSourceOptions>(
        configStream,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
        cancellationSource.Token);
    if (sourceOptions is null)
    {
        throw new InvalidDataException($"Configuration file '{configPath}' is empty or invalid.");
    }

    sourceOptions.Validate();
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(sourceOptions.RequestTimeoutSeconds) };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(sourceOptions.UserAgent);

    var linkExtractor = new UnityDocumentationLinkExtractor();
    var documentSource = new UnityScriptingApiDocumentSource(
        httpClient,
        sourceOptions,
        linkExtractor,
        loggerFactory.CreateLogger<UnityScriptingApiDocumentSource>());
    var cache = new FileDocumentCache(sourceOptions);
    var runner = new UnityDocsRag.Ingestion.IngestionRunner(
        documentSource,
        cache,
        loggerFactory.CreateLogger<UnityDocsRag.Ingestion.IngestionRunner>());

    await runner.RunAsync(cancellationSource.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
{
    logger.LogInformation("Ingestion cancelled by user");
    return 130;
}
catch (Exception exception)
{
    logger.LogError("Ingestion failed: {ErrorMessage}", exception.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
