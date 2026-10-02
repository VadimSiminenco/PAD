using Npgsql;
using Pgvector.Npgsql;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Retrieval;

namespace UnityDocsRag.Tests.Retrieval;

public sealed class PostgresVectorRetrieverValidationTests
{
    [Fact]
    public async Task NullQueryIsRejectedBeforeDatabaseAccess()
    {
        await using var dataSource = CreateUnreachableDataSource();
        var retriever = new PostgresVectorRetriever(dataSource);
        var embedding = new QueryEmbedding(new EmbeddingProfile("Ollama", "test", 3, true), new[] { 1f, 0f, 0f });

        await Assert.ThrowsAsync<ArgumentNullException>(() => retriever.RetrieveAsync(null!, embedding, CancellationToken.None));
    }

    [Fact]
    public async Task NullQueryEmbeddingIsRejectedBeforeDatabaseAccess()
    {
        await using var dataSource = CreateUnreachableDataSource();
        var retriever = new PostgresVectorRetriever(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => retriever.RetrieveAsync(new RetrievalQuery("query", 1), null!, CancellationToken.None));
    }

    [Fact]
    public async Task DimensionOutsidePgvectorRangeIsRejectedBeforeDatabaseAccess()
    {
        await using var dataSource = CreateUnreachableDataSource();
        var retriever = new PostgresVectorRetriever(dataSource);
        var profile = new EmbeddingProfile("Ollama", "test", 2001, true);
        var embedding = new QueryEmbedding(profile, new float[2001]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => retriever.RetrieveAsync(
            new RetrievalQuery("query", 1), embedding, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeDatabaseAccess()
    {
        await using var dataSource = CreateUnreachableDataSource();
        var retriever = new PostgresVectorRetriever(dataSource);
        var embedding = new QueryEmbedding(new EmbeddingProfile("Ollama", "test", 3, true), new[] { 1f, 0f, 0f });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retriever.RetrieveAsync(
            new RetrievalQuery("query", 1), embedding, cancellation.Token));
    }

    [Fact]
    public void RetrievalQueryRejectsInvalidTopKAndThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrievalQuery("query", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrievalQuery("query", 1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrievalQuery("query", 1, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrievalQuery("query", 1, -0.01));
    }

    [Fact]
    public void RetrieverOptionsRejectNonPositiveCommandTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = 0 }.Validate());
    }

    private static NpgsqlDataSource CreateUnreachableDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Database=not-used;Username=not-used;Timeout=1");
        builder.UseVector();
        return builder.Build();
    }
}
