using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Trace;
using UnityDocsRag.Api;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Tests.Api;

public sealed class LangfuseTracingTests
{
    [Fact]
    public void LangfuseIsDisabledByDefaultAndCanBeExplicitlyEnabled()
    {
        var disabled = LangfuseSettings.FromEnvironment(_ => null);
        Assert.False(disabled.Enabled);
        Assert.Equal(1d, disabled.SampleRate);

        var values = new Dictionary<string, string?>
        {
            ["LANGFUSE_ENABLED"] = "true",
            ["LANGFUSE_BASE_URL"] = "https://cloud.langfuse.com",
            ["LANGFUSE_PUBLIC_KEY"] = "public-test-placeholder",
            ["LANGFUSE_SECRET_KEY"] = "secret-test-placeholder",
            [LangfuseSettings.SampleRateEnvironmentVariable] = "0.35"
        };
        var enabled = LangfuseSettings.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.True(enabled.Enabled);
        Assert.Equal("https://cloud.langfuse.com/api/public/otel/v1/traces", enabled.Endpoint?.AbsoluteUri);
        Assert.Equal(0.35d, enabled.SampleRate);
    }

    [Fact]
    public void OtlpConfigurationCreatesHttpProtobufClientWithRequiredHeadersAndTimeout()
    {
        var publicKey = new string('p', 12);
        var secretKey = new string('s', 16);
        var values = new Dictionary<string, string?>
        {
            ["LANGFUSE_ENABLED"] = "true",
            ["LANGFUSE_BASE_URL"] = "https://cloud.langfuse.com",
            ["LANGFUSE_PUBLIC_KEY"] = publicKey,
            ["LANGFUSE_SECRET_KEY"] = secretKey
        };
        var settings = LangfuseSettings.FromEnvironment(name => values.GetValueOrDefault(name));
        var exporter = new OtlpExporterOptions();

        LangfuseTelemetry.ConfigureExporter(exporter, settings);
        using var client = exporter.HttpClientFactory();

        var actualToken = client.DefaultRequestHeaders.Authorization?.Parameter;
        var expectedToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey + ":" + secretKey));
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporter.Protocol);
        Assert.Equal("https://cloud.langfuse.com/api/public/otel/v1/traces", exporter.Endpoint.AbsoluteUri);
        Assert.Equal("Basic", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.True(AuthorizationMatches(actualToken, expectedToken));
        Assert.Equal("4", Assert.Single(client.DefaultRequestHeaders.GetValues("x-langfuse-ingestion-version")));
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
        Assert.Equal(5000, exporter.TimeoutMilliseconds);
        Assert.Equal(OpenTelemetry.ExportProcessorType.Batch, exporter.ExportProcessorType);
        Assert.Equal(256, exporter.BatchExportProcessorOptions.MaxQueueSize);
    }

    private static bool AuthorizationMatches(string? actualToken, string expectedToken)
    {
        if (actualToken is null) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(actualToken), Convert.FromBase64String(expectedToken));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [Fact]
    public async Task SpansContainOnlySafeMetadata()
    {
        var spans = new List<(string Name, string Tags)>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(LangfuseTelemetry.SourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(new CapturingExporter(spans)))
            .Build();

        var chunk = new DocumentChunk("private-chunk-id", "private-document-id",
            "private chunk text vector-marker", "Private section", 0, 5);
        var result = new RetrievedChunk(chunk,
            new Uri("https://private-source-url.invalid/private-path"), "Private title",
            0.87, 1, 0.91, 1);
        var profile = new EmbeddingProfile("Ollama", "embedding-model-safe", 3, true);
        using (var root = LangfuseTelemetry.ActivitySource.StartActivity("api.ask"))
        {
            root!.SetTag("rag.status", "Answered");
            await new TracingSemanticSearchService(new FakeSearch([result]))
                .SearchAsync(new RetrievalQuery("private question marker", 1), profile, CancellationToken.None);
            await new TracingReranker(new FakeReranker([result]), "reranker-model-safe")
                .RerankAsync(new UserQuestion("private question marker"), [result], CancellationToken.None);
            await new TracingAnswerGenerator(new FakeGenerator(new RagAnswer(
                    "private answer marker", SupportedLanguage.English, AnswerStatus.Answered,
                    [new Citation("Private title", result.SourceUrl)])), "generation-model-safe", "rag.generation.generation-model-safe")
                .GenerateAsync(new UserQuestion("private question marker"), SupportedLanguage.English,
                    [result], CancellationToken.None);
        }

        var serialized = string.Join("\n", spans.Select(span => span.Name + " " + span.Tags));
        Assert.Contains("api.ask", serialized, StringComparison.Ordinal);
        Assert.Contains("rag.semantic_search", serialized, StringComparison.Ordinal);
        Assert.Contains("rag.reranking", serialized, StringComparison.Ordinal);
        Assert.Contains("rag.generation", serialized, StringComparison.Ordinal);
        Assert.Contains("langfuse.observation.type=generation", serialized, StringComparison.Ordinal);
        Assert.Contains("langfuse.observation.model.name=generation-model-safe", serialized, StringComparison.Ordinal);
        Assert.Contains("embedding-model-safe", serialized, StringComparison.Ordinal);
        Assert.Contains("reranker-model-safe", serialized, StringComparison.Ordinal);
        Assert.Contains("generation-model-safe", serialized, StringComparison.Ordinal);
        Assert.Contains("similarity_score", serialized, StringComparison.Ordinal);
        Assert.Contains("candidate_count=1", serialized, StringComparison.Ordinal);
        Assert.Contains("source_count=1", serialized, StringComparison.Ordinal);
        Assert.Contains("rag.language=English", serialized, StringComparison.Ordinal);
        Assert.Contains("rag.status=Answered", serialized, StringComparison.Ordinal);
        Assert.Contains("duration_ms", serialized, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "private question marker", "private answer marker", "private chunk text", "vector-marker",
                     "private-source-url", "private-path", "private-chunk-id", "private-document-id", "Private title"
                 })
            Assert.DoesNotContain(forbidden, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskAnswerIsReturnedWhenTraceExportReportsFailure()
    {
        var exporter = new FailingExporter();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(LangfuseTelemetry.SourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();
        var service = new FakeRagQueryService((_, _) => Task.FromResult(new RagAnswer(
            "Ответ", SupportedLanguage.Russian, AnswerStatus.Answered,
            [new Citation("Title", new Uri("https://docs.example/"))])));

        var result = await AskApiEndpoint.HandleAsync(new AskRequest("question"), service,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AskApiEndpoint>.Instance, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<AskResponse>>(result);
        Assert.True(exporter.WasInvoked);
    }

    private sealed class FakeSearch(IReadOnlyList<RetrievedChunk> results) : ISemanticSearchService
    {
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, EmbeddingProfile profile,
            CancellationToken cancellationToken) => Task.FromResult(results);
    }

    private sealed class FakeReranker(IReadOnlyList<RetrievedChunk> results) : IReranker
    {
        public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(UserQuestion question,
            IReadOnlyList<RetrievedChunk> candidates, CancellationToken cancellationToken) => Task.FromResult(results);
    }

    private sealed class FakeGenerator(RagAnswer answer) : IAnswerGenerator
    {
        public Task<RagAnswer> GenerateAsync(UserQuestion question, SupportedLanguage language,
            IReadOnlyList<RetrievedChunk> evidence, CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    private sealed class FakeRagQueryService(Func<UserQuestion, CancellationToken, Task<RagAnswer>> handler)
        : IRagQueryService
    {
        public int CallCount { get; private set; }
        public Task<RagAnswer> AnswerAsync(UserQuestion question, CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(question, cancellationToken);
        }
    }

    private sealed class CapturingExporter(List<(string Name, string Tags)> spans) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                var tags = string.Join(";", activity.TagObjects.Select(tag =>
                    tag.Key + "=" + Convert.ToString(tag.Value, CultureInfo.InvariantCulture)));
                spans.Add((activity.DisplayName, tags));
            }
            return ExportResult.Success;
        }
    }

    private sealed class FailingExporter : BaseExporter<Activity>
    {
        public bool WasInvoked { get; private set; }
        public override ExportResult Export(in Batch<Activity> batch)
        {
            WasInvoked = true;
            return ExportResult.Failure;
        }
    }
}
