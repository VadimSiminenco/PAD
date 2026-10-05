using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UnityDocsRag.Api;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Ingestion;
using UnityDocsRag.Infrastructure.Embeddings;
using UnityDocsRag.Infrastructure.Generation;
using UnityDocsRag.Infrastructure.Query;
using UnityDocsRag.Infrastructure.Reranking;
using UnityDocsRag.Infrastructure.Retrieval;
using UnityDocsRag.Infrastructure.Storage;

var configPath = Path.Combine(Directory.GetCurrentDirectory(), "configs", "ask.json");
await using var configStream = File.OpenRead(configPath);
var options = await JsonSerializer.DeserializeAsync<AskRunOptions>(configStream,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("Ask configuration is empty or invalid.");
options.Validate();

var postgresOptions = new PostgresVectorStoreOptions
{
    ConnectionString = options.GetPostgresConnectionString(),
    CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds
};

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5187");
LangfuseTelemetry.Configure(builder);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(postgresOptions);
builder.Services.AddSingleton<NpgsqlDataSource>(_ => postgresOptions.CreateDataSource());
builder.Services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<IRagQueryService>(services =>
{
    var httpClient = services.GetRequiredService<HttpClient>();
    var semanticSearch = new SemanticSearchService(
        new OllamaEmbeddingProvider(httpClient, options.CreateOllamaEmbeddingOptions()),
        new PostgresVectorRetriever(services.GetRequiredService<NpgsqlDataSource>(),
            new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds }));
    var tracedSearch = new TracingSemanticSearchService(semanticSearch);
    var rerankerOptions = options.CreateOllamaRerankerOptions();
    return new RagQueryService(
        new RussianEnglishLanguageDetector(),
        tracedSearch,
        new TracingReranker(new OllamaReranker(httpClient, rerankerOptions), rerankerOptions.Model),
        new TracingAnswerGenerator(new OllamaAnswerGenerator(httpClient, options.CreateOllamaGenerationOptions()),
            options.GenerationModel),
        services.GetRequiredService<ILogger<RagQueryService>>(),
        options.CreateEmbeddingProfile(),
        options.TopK,
        options.DomainSimilarityThreshold,
        options.EvidenceSimilarityThreshold,
        options.MaxEvidenceChunks);
});
builder.Services.AddSingleton<IModelComparisonSnapshotStore>(_ => new FileModelComparisonSnapshotStore(
    Path.Combine(Directory.GetCurrentDirectory(), "data", "state", "generation-ab-last.json")));
builder.Services.AddSingleton<ModelComparisonService>(services =>
{
    var httpClient = services.GetRequiredService<HttpClient>();
    var semanticSearch = new SemanticSearchService(
        new OllamaEmbeddingProvider(httpClient, options.CreateOllamaEmbeddingOptions()),
        new PostgresVectorRetriever(services.GetRequiredService<NpgsqlDataSource>(),
            new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = options.PostgresCommandTimeoutSeconds }));
    var rerankerOptions = options.CreateOllamaRerankerOptions();
    var commonGeneration = options.CreateOllamaGenerationOptions();
    OllamaGenerationOptions ForModel(string model) => new()
    {
        Endpoint = commonGeneration.Endpoint,
        Model = model,
        KeepAlive = commonGeneration.KeepAlive,
        HttpTimeoutSeconds = commonGeneration.HttpTimeoutSeconds,
        Temperature = commonGeneration.Temperature,
        NumPredict = commonGeneration.NumPredict,
        NumCtx = commonGeneration.NumCtx
    };
    var modelA = "qwen3:4b";
    var modelB = "qwen3:4b-instruct";
    return new ModelComparisonService(
        new RussianEnglishLanguageDetector(),
        new TracingSemanticSearchService(semanticSearch),
        new TracingReranker(new OllamaReranker(httpClient, rerankerOptions), rerankerOptions.Model),
        options.CreateEmbeddingProfile(), options.TopK, options.DomainSimilarityThreshold,
        options.EvidenceSimilarityThreshold, options.MaxEvidenceChunks,
        [
            new ModelComparisonRun(modelA, new TracingAnswerGenerator(
                new OllamaAnswerGenerator(httpClient, ForModel(modelA)), modelA, "rag.generation.qwen3_4b")),
            new ModelComparisonRun(modelB, new TracingAnswerGenerator(
                new OllamaAnswerGenerator(httpClient, ForModel(modelB)), modelB, "rag.generation.qwen3_4b_instruct"))
        ]);
});

var app = builder.Build();
_ = app.Services.GetRequiredService<IRagQueryService>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapPost("/api/ask", AskApiEndpoint.HandleAsync);
app.MapPost("/api/compare", ModelComparisonEndpoint.HandleAsync);
app.MapGet("/api/compare/last", ModelComparisonEndpoint.GetLastAsync);
app.Run();
