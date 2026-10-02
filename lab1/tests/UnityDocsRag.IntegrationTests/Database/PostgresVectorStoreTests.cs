using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Pgvector;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Storage;

namespace UnityDocsRag.IntegrationTests.Database;

public sealed class PostgresVectorStoreTests
{
    private const string ProfileKey = "ollama:embeddinggemma:768";

    [Fact]
    public async Task UpsertUpdatesSnapshotAndSupportsCosineSearchWhenDatabaseIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // A real database run is performed manually by setting UNITYDOCS_TEST_CONNECTION_STRING.
            return;
        }

        var options = new PostgresVectorStoreOptions { ConnectionString = connectionString };
        await using var dataSource = options.CreateDataSource();
        var store = new PostgresVectorStore(dataSource, options);
        var documentId = Hash(Guid.NewGuid().ToString("N"));
        var chunkAId = Hash(Guid.NewGuid().ToString("N"));
        var chunkBId = Hash(Guid.NewGuid().ToString("N"));
        var profile = new EmbeddingProfile("Ollama", "embeddinggemma", 768, multilingual: true);
        bool profileExisted = false;
        try
        {
            await using (var setupConnection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM rag.embedding_profiles WHERE profile_key = @key)", setupConnection))
            {
                command.Parameters.AddWithValue("key", ProfileKey);
                profileExisted = (bool)(await command.ExecuteScalarAsync())!;
            }

            var document = MakeDocument(documentId, "initial document content");
            var chunkA = MakeChunk(chunkAId, documentId, 0, "initial chunk alpha");
            var chunkB = MakeChunk(chunkBId, documentId, 1, "initial chunk beta");
            await store.UpsertAsync(new[] { document }, new[] { chunkA, chunkB }, new[]
            {
                MakeEmbedding(chunkA, profile, axis: 0), MakeEmbedding(chunkB, profile, axis: 1)
            }, CancellationToken.None);

            document = MakeDocument(documentId, "updated document content");
            chunkA = MakeChunk(chunkAId, documentId, 0, "updated chunk alpha");
            chunkB = MakeChunk(chunkBId, documentId, 1, "updated chunk beta");
            await store.UpsertAsync(new[] { document }, new[] { chunkA, chunkB }, new[]
            {
                MakeEmbedding(chunkA, profile, axis: 0), MakeEmbedding(chunkB, profile, axis: 1)
            }, CancellationToken.None);

            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.documents WHERE document_id = @id", ("id", documentId)));
                Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunks WHERE document_id = @id", ("id", documentId)));
                Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunk_embeddings e JOIN rag.chunks c USING (chunk_id) WHERE c.document_id = @id", ("id", documentId)));
                Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.embedding_profiles WHERE profile_key = @key", ("key", ProfileKey)));
                Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(DISTINCT chunk_id) FROM rag.chunks WHERE document_id = @id", ("id", documentId)));
                Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM (SELECT document_id, ordinal FROM rag.chunks WHERE document_id = @id GROUP BY document_id, ordinal HAVING count(*) > 1) duplicates", ("id", documentId)));
                Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM (SELECT e.chunk_id, e.profile_key FROM rag.chunk_embeddings e JOIN rag.chunks c USING (chunk_id) WHERE c.document_id = @id GROUP BY e.chunk_id, e.profile_key HAVING count(*) > 1) duplicates", ("id", documentId)));
                Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.documents WHERE document_id = @id AND content = 'updated document content'", ("id", documentId)));
                Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunks WHERE document_id = @id AND content LIKE 'updated chunk %'", ("id", documentId)));
                Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunk_embeddings e JOIN rag.chunks c USING (chunk_id) WHERE c.document_id = @id AND vector_dims(e.embedding) = 768", ("id", documentId)));
            }

            var replacementChunkId = Hash(Guid.NewGuid().ToString("N"));
            var replacementChunk = MakeChunk(replacementChunkId, documentId, 0, "snapshot replacement chunk");
            await store.UpsertAsync(new[] { document }, new[] { replacementChunk },
                new[] { MakeEmbedding(replacementChunk, profile, axis: 0) }, CancellationToken.None);

            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunks WHERE chunk_id = @id", ("id", chunkAId)));
                Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunks WHERE chunk_id = @id", ("id", chunkBId)));
                Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunk_embeddings WHERE chunk_id = @id", ("id", chunkAId)));
                Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunk_embeddings WHERE chunk_id = @id", ("id", chunkBId)));
                Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunks WHERE chunk_id = @id AND content = 'snapshot replacement chunk'", ("id", replacementChunkId)));
                Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunks WHERE document_id = @id", ("id", documentId)));
                Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM rag.chunk_embeddings e JOIN rag.chunks c USING (chunk_id) WHERE c.document_id = @id", ("id", documentId)));

                await using var nearest = new NpgsqlCommand("""
                    SELECT e.chunk_id
                    FROM rag.chunk_embeddings e
                    JOIN rag.chunks c USING (chunk_id)
                    WHERE e.profile_key = 'ollama:embeddinggemma:768'
                      AND c.document_id = @document_id
                    ORDER BY embedding::vector(768) <=> @query
                    LIMIT 1;
                    """, connection);
                nearest.Parameters.AddWithValue("document_id", documentId);
                nearest.Parameters.AddWithValue("query", new Vector(AxisVector(0)));
                Assert.Equal(replacementChunkId, await nearest.ExecuteScalarAsync());
            }
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            await using (var deleteDocument = new NpgsqlCommand("DELETE FROM rag.documents WHERE document_id = @id", cleanup))
            {
                deleteDocument.Parameters.AddWithValue("id", documentId);
                await deleteDocument.ExecuteNonQueryAsync();
            }
            if (!profileExisted)
            {
                await using var deleteProfile = new NpgsqlCommand("""
                    DELETE FROM rag.embedding_profiles p
                    WHERE p.profile_key = @key
                      AND NOT EXISTS (SELECT 1 FROM rag.chunk_embeddings e WHERE e.profile_key = p.profile_key);
                    """, cleanup);
                deleteProfile.Parameters.AddWithValue("key", ProfileKey);
                await deleteProfile.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task UpsertRollsBackProfileWhenDocumentInsertFailsWhenDatabaseIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = new PostgresVectorStoreOptions { ConnectionString = connectionString };
        await using var dataSource = options.CreateDataSource();
        var store = new PostgresVectorStore(dataSource, options);
        var documentId = Hash(Guid.NewGuid().ToString("N"));
        var profile = new EmbeddingProfile("Ollama", $"rollback-{Guid.NewGuid():N}", 768, multilingual: true);
        var profileKey = EmbeddingProfileKey.Create(profile);
        var chunk = MakeChunk(Hash(Guid.NewGuid().ToString("N")), documentId, 0, "rollback validation chunk");
        var invalidDocument = new ProcessedDocument(documentId,
            new Uri($"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/rollback-{documentId}.html"),
            "Rollback document", "6000.3", "rollback test content", new string('a', 63), DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["test"] = "rollback" });

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => store.UpsertAsync(new[] { invalidDocument }, new[] { chunk },
                new[] { MakeEmbedding(chunk, profile, axis: 0) }, CancellationToken.None));

            await using var verification = await dataSource.OpenConnectionAsync();
            Assert.Equal(0L, await ScalarLongAsync(verification,
                "SELECT count(*) FROM rag.documents WHERE document_id = @id", ("id", documentId)));
            Assert.Equal(0L, await ScalarLongAsync(verification,
                "SELECT count(*) FROM rag.embedding_profiles WHERE profile_key = @key", ("key", profileKey)));
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            await using (var deleteDocument = new NpgsqlCommand("DELETE FROM rag.documents WHERE document_id = @id", cleanup))
            {
                deleteDocument.Parameters.AddWithValue("id", documentId);
                await deleteDocument.ExecuteNonQueryAsync();
            }
            await using var deleteProfile = new NpgsqlCommand("DELETE FROM rag.embedding_profiles WHERE profile_key = @key", cleanup);
            deleteProfile.Parameters.AddWithValue("key", profileKey);
            await deleteProfile.ExecuteNonQueryAsync();
        }
    }

    private static ProcessedDocument MakeDocument(string id, string content) => new(id,
        new Uri($"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/integration-{id}.html"),
        "Integration document", "6000.3", content, Hash(content), DateTimeOffset.UtcNow,
        new Dictionary<string, string> { ["test"] = "integration" });

    private static DocumentChunk MakeChunk(string id, string documentId, int ordinal, string text) =>
        new(id, documentId, text, "Integration", ordinal, 2);

    private static ChunkEmbedding MakeEmbedding(DocumentChunk chunk, EmbeddingProfile profile, int axis) =>
        new(chunk, profile, AxisVector(axis));

    private static float[] AxisVector(int axis)
    {
        var vector = new float[768];
        vector[axis] = 1f;
        return vector;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
