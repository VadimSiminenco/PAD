using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Ingestion;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Tests.Ingestion;

public sealed class IndexingRunnerTests
{
    private static readonly EmbeddingProfile Profile = new("Ollama", "embeddinggemma", 3, multilingual: true);

    [Fact]
    public async Task EmbedsThenUpsertsAndReturnsSummary()
    {
        var order = new List<string>();
        var (snapshot, embeddings) = MakeInput();
        var provider = new FakeEmbeddingProvider((chunks, profile, _) =>
        {
            order.Add("embed");
            Assert.Same(snapshot.Chunks, chunks);
            Assert.Same(Profile, profile);
            return Task.FromResult<IReadOnlyList<ChunkEmbedding>>(embeddings);
        });
        var store = new FakeVectorStore((documents, chunks, actualEmbeddings, _) =>
        {
            order.Add("store");
            Assert.Same(snapshot.Documents, documents);
            Assert.Same(snapshot.Chunks, chunks);
            Assert.Same(embeddings, actualEmbeddings);
            return Task.CompletedTask;
        });
        var runner = new IndexingRunner(provider, store, new CapturingLogger());
        var summary = await runner.RunAsync(snapshot, Profile, CancellationToken.None);
        Assert.Equal(new[] { "embed", "store" }, order);
        Assert.Equal(new IndexingRunSummary(1, 2, 2, "ollama:embeddinggemma:3", 3), summary);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task ProviderFailureDoesNotCallStore()
    {
        var (snapshot, _) = MakeInput();
        var store = new FakeVectorStore();
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromException<IReadOnlyList<ChunkEmbedding>>(new InvalidOperationException("provider failed"))), store, new CapturingLogger());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(snapshot, Profile, CancellationToken.None));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task CountMismatchIsRejectedBeforeStore()
    {
        var (snapshot, embeddings) = MakeInput();
        var store = new FakeVectorStore();
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(embeddings.Take(1).ToArray())), store, new CapturingLogger());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(snapshot, Profile, CancellationToken.None));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ReorderedEmbeddingsAreRejectedBeforeStore()
    {
        var (snapshot, embeddings) = MakeInput();
        var store = new FakeVectorStore();
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(embeddings.Reverse().ToArray())), store, new CapturingLogger());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(snapshot, Profile, CancellationToken.None));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task WrongProfileIsRejectedBeforeStore()
    {
        var (snapshot, embeddings) = MakeInput();
        var wrong = new EmbeddingProfile("other", "embeddinggemma", 3, multilingual: true);
        var changed = embeddings.Select(item => new ChunkEmbedding(item.Chunk, wrong, item.Vector)).ToArray();
        var store = new FakeVectorStore();
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(changed)), store, new CapturingLogger());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(snapshot, Profile, CancellationToken.None));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task WrongDimensionProfileIsRejectedBeforeStore()
    {
        var (snapshot, embeddings) = MakeInput();
        var wrong = new EmbeddingProfile("Ollama", "embeddinggemma", 2, multilingual: true);
        var changed = embeddings.Select(item => new ChunkEmbedding(item.Chunk, wrong, new[] { 1f, 0f })).ToArray();
        var store = new FakeVectorStore();
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(changed)), store, new CapturingLogger());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(snapshot, Profile, CancellationToken.None));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task StoreFailureIsNotHidden()
    {
        var (snapshot, embeddings) = MakeInput();
        var expected = new InvalidOperationException("store failed");
        var store = new FakeVectorStore((_, _, _, _) => Task.FromException(expected));
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(embeddings)), store, new CapturingLogger());
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(snapshot, Profile, CancellationToken.None));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CancellationIsPassedToProviderAndPreventsStore()
    {
        var (snapshot, _) = MakeInput();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new FakeEmbeddingProvider((_, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ChunkEmbedding>>(Array.Empty<ChunkEmbedding>());
        });
        var store = new FakeVectorStore();
        var runner = new IndexingRunner(provider, store, new CapturingLogger());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(snapshot, Profile, cancellation.Token));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task EmptySnapshotIsRejected()
    {
        var store = new FakeVectorStore();
        var provider = new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(Array.Empty<ChunkEmbedding>()));
        var runner = new IndexingRunner(provider, store, new CapturingLogger());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new ProcessingArtifactSnapshot(Array.Empty<ProcessedDocument>(), Array.Empty<DocumentChunk>(), "6000.3", 512, 64), Profile, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task LogsDoNotContainDocumentTextOrVectorValues()
    {
        var (snapshot, embeddings) = MakeInput();
        var logger = new CapturingLogger();
        var runner = new IndexingRunner(new FakeEmbeddingProvider((_, _, _) => Task.FromResult<IReadOnlyList<ChunkEmbedding>>(embeddings)), new FakeVectorStore(), logger);
        await runner.RunAsync(snapshot, Profile, CancellationToken.None);
        var output = string.Join(" ", logger.Messages);
        Assert.Contains("Indexing complete", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE DOCUMENT BODY", output, StringComparison.Ordinal);
        Assert.DoesNotContain("123.456", output, StringComparison.Ordinal);
    }

    private static (ProcessingArtifactSnapshot Snapshot, ChunkEmbedding[] Embeddings) MakeInput()
    {
        var document = new ProcessedDocument("doc-1", new Uri("https://example.test/api"), "API", "6000.3",
            "PRIVATE DOCUMENT BODY", "hash", DateTimeOffset.UtcNow, new Dictionary<string, string>());
        var documents = Array.AsReadOnly(new[] { document });
        var chunks = Array.AsReadOnly(new[]
        {
            new DocumentChunk("chunk-1", "doc-1", "PRIVATE DOCUMENT BODY", "Description", 0, 3),
            new DocumentChunk("chunk-2", "doc-1", "another private text", "Methods", 1, 3)
        });
        var snapshot = new ProcessingArtifactSnapshot(documents, chunks, "6000.3", 512, 64);
        var embeddings = chunks.Select((chunk, index) => new ChunkEmbedding(chunk, Profile,
            index == 0 ? new[] { 123.456f, 0f, 1f } : new[] { 0f, 1f, 0f })).ToArray();
        return (snapshot, embeddings);
    }

    private sealed class FakeEmbeddingProvider(Func<IReadOnlyList<DocumentChunk>, EmbeddingProfile, CancellationToken, Task<IReadOnlyList<ChunkEmbedding>>> embed) : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ChunkEmbedding>> EmbedAsync(IReadOnlyList<DocumentChunk> chunks, EmbeddingProfile profile, CancellationToken cancellationToken)
        {
            Calls++;
            return embed(chunks, profile, cancellationToken);
        }
    }

    private sealed class FakeVectorStore(Func<IReadOnlyList<ProcessedDocument>, IReadOnlyList<DocumentChunk>, IReadOnlyList<ChunkEmbedding>, CancellationToken, Task>? upsert = null) : IVectorStore
    {
        public int Calls { get; private set; }
        public Task UpsertAsync(IReadOnlyList<ProcessedDocument> documents, IReadOnlyList<DocumentChunk> chunks,
            IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken)
        {
            Calls++;
            return upsert?.Invoke(documents, chunks, embeddings, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<IndexingRunner>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
