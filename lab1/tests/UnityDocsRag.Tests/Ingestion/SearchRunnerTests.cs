using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Ingestion;

namespace UnityDocsRag.Tests.Ingestion;

public sealed class SearchRunnerTests
{
    [Fact]
    public async Task CreatesConfiguredRetrievalQueryAndReturnsServiceResults()
    {
        var profile = new EmbeddingProfile("Ollama", "embeddinggemma", 768, true);
        var expected = Array.AsReadOnly(new[] { MakeResult("first", 1), MakeResult("second", 2) });
        var service = new FakeSemanticSearchService((query, actualProfile, _) =>
        {
            Assert.Equal("How do I use a NavMeshAgent?", query.Text);
            Assert.Equal(7, query.TopK);
            Assert.Equal(0.42, query.SimilarityThreshold);
            Assert.Same(profile, actualProfile);
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(expected);
        });
        var runner = new SearchRunner(service, profile, new CapturingLogger());

        var results = await runner.RunAsync("How do I use a NavMeshAgent?", 7, 0.42, CancellationToken.None);

        Assert.Same(expected, results);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task LogsOnlySafeResultMetadataAndNeverQuestionChunkOrVector()
    {
        const string question = "PRIVATE QUESTION text";
        const string chunkText = "PRIVATE CHUNK BODY vector 0.123456789";
        var expected = Array.AsReadOnly(new[] { MakeResult("safe result", 1, chunkText) });
        var service = new FakeSemanticSearchService((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(expected));
        var logger = new CapturingLogger();
        var runner = new SearchRunner(service, new EmbeddingProfile("Ollama", "embeddinggemma", 768, true), logger);

        await runner.RunAsync(question, 5, 0, CancellationToken.None);

        var logged = string.Join(" ", logger.Messages);
        Assert.Contains("1 result", logged, StringComparison.Ordinal);
        Assert.Contains("Unity NavMesh API", logged, StringComparison.Ordinal);
        Assert.Contains("Methods", logged, StringComparison.Ordinal);
        Assert.Contains("docs.unity3d.com", logged, StringComparison.Ordinal);
        Assert.Contains("0.8750", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(question, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE CHUNK BODY", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("0.123456789", logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n")]
    public async Task EmptyQuestionIsRejectedBeforeServiceCall(string? question)
    {
        var service = new FakeSemanticSearchService((_, _, _) => throw new InvalidOperationException("Should not be called."));
        var runner = new SearchRunner(service, new EmbeddingProfile("Ollama", "model", 3, true), new CapturingLogger());

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(question!, 5, 0, CancellationToken.None));
        Assert.Equal(0, service.Calls);
    }

    private static RetrievedChunk MakeResult(string id, int rank, string? text = null) => new(
        new DocumentChunk(id, "doc", text ?? id, "Methods", rank - 1, 2),
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/NavMeshAgent.html"),
        "Unity NavMesh API", 0.875, rank);

    private sealed class FakeSemanticSearchService(Func<RetrievalQuery, EmbeddingProfile, CancellationToken, Task<IReadOnlyList<RetrievedChunk>>> search)
        : ISemanticSearchService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, EmbeddingProfile profile,
            CancellationToken cancellationToken)
        {
            Calls++;
            return search(query, profile, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<SearchRunner>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
