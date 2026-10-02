using System.Security.Cryptography;
using System.Text;
using Npgsql;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Retrieval;
using UnityDocsRag.Infrastructure.Storage;

namespace UnityDocsRag.IntegrationTests.Retrieval;

public sealed class PostgresVectorRetrieverTests
{
    [Fact]
    public async Task RetrievalFiltersProfileRanksCosineAndAppliesTopKAndThresholdWhenDatabaseIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var storeOptions = new PostgresVectorStoreOptions { ConnectionString = connectionString };
        await using var dataSource = storeOptions.CreateDataSource();
        var store = new PostgresVectorStore(dataSource, storeOptions);
        var retriever = new PostgresVectorRetriever(dataSource, new PostgresVectorRetrieverOptions { CommandTimeoutSeconds = 30 });
        var runId = Guid.NewGuid().ToString("N");
        var profileA = new EmbeddingProfile("Ollama", $"retrieval-a-{runId}", 3, multilingual: true);
        var profileB = new EmbeddingProfile("Ollama", $"retrieval-b-{runId}", 3, multilingual: true);
        var noDataProfile = new EmbeddingProfile("Ollama", $"retrieval-empty-{runId}", 3, multilingual: true);
        var documentIds = Enumerable.Range(0, 3).Select(index => Hash($"{runId}/document/{index}")).ToArray();
        var chunks = Enumerable.Range(0, 3).Select(index => new DocumentChunk(
            Hash($"{runId}/chunk/{index}"), documentIds[index], $"result chunk {index}", $"Section {index}", index, 3)).ToArray();
        var documents = Enumerable.Range(0, 3).Select(index => new ProcessedDocument(
            documentIds[index],
            new Uri($"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/retrieval/{runId}/{index}.html"),
            $"Retrieval test document {index}", "6000.3", $"document body {index}", Hash($"document body {index}"),
            DateTimeOffset.UtcNow, new Dictionary<string, string> { ["runId"] = runId, ["index"] = index.ToString() })).ToArray();

        try
        {
            await store.UpsertAsync(documents, chunks,
                new[] { MakeEmbedding(chunks[0], profileA, 1f, 0f, 0f), MakeEmbedding(chunks[1], profileA, 0.8f, 0.6f, 0f), MakeEmbedding(chunks[2], profileA, 0f, 1f, 0f) },
                CancellationToken.None);
            // Deliberately store different vectors for the same chunks under another profile.
            await store.UpsertAsync(documents, chunks,
                chunks.Select(chunk => MakeEmbedding(chunk, profileB, 1f, 0f, 0f)).ToArray(), CancellationToken.None);

            var queryVector = new QueryEmbedding(profileA, new[] { 1f, 0f, 0f });
            var allMatches = await retriever.RetrieveAsync(new RetrievalQuery("test query", 10), queryVector, CancellationToken.None);
            Assert.Equal(3, allMatches.Count);
            Assert.Equal(chunks.Select(chunk => chunk.ChunkId), allMatches.Select(result => result.Chunk.ChunkId));
            Assert.Equal(1d, allMatches[0].SimilarityScore, precision: 5);
            Assert.Equal(0.8d, allMatches[1].SimilarityScore, precision: 5);
            Assert.Equal(0d, allMatches[2].SimilarityScore, precision: 5);
            Assert.Equal(new[] { 1, 2, 3 }, allMatches.Select(result => result.InitialRank));

            var topTwo = await retriever.RetrieveAsync(new RetrievalQuery("test query", 2), queryVector, CancellationToken.None);
            Assert.Equal(new[] { chunks[0].ChunkId, chunks[1].ChunkId }, topTwo.Select(result => result.Chunk.ChunkId));

            var thresholdMatches = await retriever.RetrieveAsync(new RetrievalQuery("test query", 10, 0.9), queryVector, CancellationToken.None);
            Assert.Single(thresholdMatches);
            Assert.Equal(chunks[0].ChunkId, thresholdMatches[0].Chunk.ChunkId);

            var otherProfileMatches = await retriever.RetrieveAsync(new RetrievalQuery("test query", 10),
                new QueryEmbedding(noDataProfile, new[] { 1f, 0f, 0f }), CancellationToken.None);
            Assert.Empty(otherProfileMatches);

            var best = allMatches[0];
            Assert.Equal(chunks[0], best.Chunk);
            Assert.Equal(documents[0].CanonicalUrl, best.SourceUrl);
            Assert.Equal(documents[0].Title, best.SourceTitle);
            Assert.Equal("result chunk 0", best.Chunk.Text);
            Assert.Equal("Section 0", best.Chunk.Section);
            Assert.Equal(0, best.Chunk.Ordinal);
            Assert.Equal(3, best.Chunk.ApproximateTokenCount);
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            foreach (var documentId in documentIds)
            {
                await using var command = new NpgsqlCommand("DELETE FROM rag.documents WHERE document_id = @document_id", cleanup);
                command.Parameters.AddWithValue("document_id", documentId);
                await command.ExecuteNonQueryAsync();
            }
            foreach (var profile in new[] { profileA, profileB, noDataProfile })
            {
                await using var command = new NpgsqlCommand("DELETE FROM rag.embedding_profiles WHERE profile_key = @profile_key", cleanup);
                command.Parameters.AddWithValue("profile_key", EmbeddingProfileKey.Create(profile));
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static ChunkEmbedding MakeEmbedding(DocumentChunk chunk, EmbeddingProfile profile, params float[] vector) =>
        new(chunk, profile, vector);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
