using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Retrieval;

namespace UnityDocsRag.Tests.Retrieval;

public sealed class SemanticSearchServiceTests
{
    [Fact]
    public async Task EmbedsOnceThenPassesOriginalQueryAndEmbeddingToRetrieverWithoutReordering()
    {
        var profile = new EmbeddingProfile("Ollama", "embeddinggemma", 3, true);
        var query = new RetrievalQuery("original question", 4, 0.25);
        var queryEmbedding = new QueryEmbedding(profile, new[] { 0.1f, 0.2f, 0.3f });
        var expected = new[] { MakeResult("first", 1), MakeResult("second", 2) };
        var results = Array.AsReadOnly(expected);
        var provider = new FakeQueryEmbeddingProvider((text, actualProfile, _) =>
        {
            Assert.Equal("original question", text);
            Assert.Same(profile, actualProfile);
            return Task.FromResult(queryEmbedding);
        });
        var retriever = new FakeRetriever((actualQuery, actualEmbedding, _) =>
        {
            Assert.Same(query, actualQuery);
            Assert.Same(queryEmbedding, actualEmbedding);
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(results);
        });
        var service = new SemanticSearchService(provider, retriever);

        var actual = await service.SearchAsync(query, profile, CancellationToken.None);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, retriever.Calls);
        Assert.Same(results, actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ProviderFailurePreventsRetrieverCall()
    {
        var providerError = new InvalidOperationException("embedding failed");
        var provider = new FakeQueryEmbeddingProvider((_, _, _) => Task.FromException<QueryEmbedding>(providerError));
        var retriever = new FakeRetriever((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(Array.Empty<RetrievedChunk>()));
        var service = new SemanticSearchService(provider, retriever);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(
            new RetrievalQuery("question", 3), new EmbeddingProfile("Ollama", "model", 2, true), CancellationToken.None));

        Assert.Same(providerError, actual);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, retriever.Calls);
    }

    [Fact]
    public async Task CancellationTokenIsPassedToProviderAndRetriever()
    {
        using var cancellation = new CancellationTokenSource();
        var profile = new EmbeddingProfile("Ollama", "model", 2, true);
        var embedding = new QueryEmbedding(profile, new[] { 1f, 0f });
        CancellationToken providerToken = default;
        CancellationToken retrieverToken = default;
        var provider = new FakeQueryEmbeddingProvider((_, _, token) =>
        {
            providerToken = token;
            return Task.FromResult(embedding);
        });
        var retriever = new FakeRetriever((_, _, token) =>
        {
            retrieverToken = token;
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Array.Empty<RetrievedChunk>());
        });
        var service = new SemanticSearchService(provider, retriever);

        await service.SearchAsync(new RetrievalQuery("question", 1), profile, cancellation.Token);

        Assert.Equal(cancellation.Token, providerToken);
        Assert.Equal(cancellation.Token, retrieverToken);
    }

    [Fact]
    public async Task NullArgumentsAreRejectedBeforeProviderCall()
    {
        var provider = new FakeQueryEmbeddingProvider((_, _, _) => throw new InvalidOperationException());
        var retriever = new FakeRetriever((_, _, _) => throw new InvalidOperationException());
        var service = new SemanticSearchService(provider, retriever);
        var profile = new EmbeddingProfile("Ollama", "model", 2, true);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SearchAsync(null!, profile, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SearchAsync(new RetrievalQuery("q", 1), null!, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    private static RetrievedChunk MakeResult(string id, int rank) => new(
        new DocumentChunk(id, "doc", id, null, rank - 1, 1),
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/test.html"), "Test document", 0.9, rank);

    private sealed class FakeQueryEmbeddingProvider(Func<string, EmbeddingProfile, CancellationToken, Task<QueryEmbedding>> embed)
        : IQueryEmbeddingProvider
    {
        public int Calls { get; private set; }
        public Task<QueryEmbedding> EmbedQueryAsync(string question, EmbeddingProfile profile, CancellationToken cancellationToken)
        {
            Calls++;
            return embed(question, profile, cancellationToken);
        }
    }

    private sealed class FakeRetriever(Func<RetrievalQuery, QueryEmbedding, CancellationToken, Task<IReadOnlyList<RetrievedChunk>>> retrieve)
        : IRetriever
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(RetrievalQuery query, QueryEmbedding queryEmbedding,
            CancellationToken cancellationToken)
        {
            Calls++;
            return retrieve(query, queryEmbedding, cancellationToken);
        }
    }
}
