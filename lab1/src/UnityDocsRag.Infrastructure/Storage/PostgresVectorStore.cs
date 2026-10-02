using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;

namespace UnityDocsRag.Infrastructure.Storage;

public sealed class PostgresVectorStore : IVectorStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresVectorStoreOptions _options;

    public PostgresVectorStore(NpgsqlDataSource dataSource, PostgresVectorStoreOptions options)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task UpsertAsync(IReadOnlyList<ProcessedDocument> documents, IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken)
    {
        var profile = ValidateInput(documents, chunks, embeddings);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpsertProfileAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
            foreach (var document in documents.OrderBy(value => value.DocumentId, StringComparer.Ordinal))
                await UpsertDocumentAsync(connection, transaction, document, cancellationToken).ConfigureAwait(false);

            // Remove superseded IDs first: a replacement chunk can reuse the same (document_id, ordinal).
            foreach (var document in documents.OrderBy(value => value.DocumentId, StringComparer.Ordinal))
            {
                var currentIds = chunks.Where(chunk => string.Equals(chunk.DocumentId, document.DocumentId, StringComparison.Ordinal))
                    .Select(chunk => chunk.ChunkId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                await DeleteStaleChunksAsync(connection, transaction, document.DocumentId, currentIds, cancellationToken).ConfigureAwait(false);
            }

            var chunksByDocument = chunks.OrderBy(chunk => chunk.DocumentId, StringComparer.Ordinal)
                .ThenBy(chunk => chunk.Ordinal).GroupBy(chunk => chunk.DocumentId, StringComparer.Ordinal);
            foreach (var chunk in chunksByDocument.SelectMany(group => group))
                await UpsertChunkAsync(connection, transaction, chunk, cancellationToken).ConfigureAwait(false);

            foreach (var embedding in embeddings.OrderBy(value => value.Chunk.DocumentId, StringComparer.Ordinal)
                         .ThenBy(value => value.Chunk.Ordinal).ThenBy(value => value.Chunk.ChunkId, StringComparer.Ordinal))
                await UpsertEmbeddingAsync(connection, transaction, embedding, profile, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original failure; disposing the transaction is a final rollback safeguard. */ }
            throw;
        }
    }

    private static EmbeddingProfile ValidateInput(IReadOnlyList<ProcessedDocument> documents,
        IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<ChunkEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddings);
        if (documents.Count == 0) throw new ArgumentException("At least one document is required.", nameof(documents));
        if (chunks.Count == 0) throw new ArgumentException("At least one chunk is required.", nameof(chunks));
        if (embeddings.Count == 0) throw new ArgumentException("At least one embedding is required.", nameof(embeddings));
        if (documents.Any(value => value is null)) throw new ArgumentException("Document collection contains a null item.", nameof(documents));
        if (chunks.Any(value => value is null)) throw new ArgumentException("Chunk collection contains a null item.", nameof(chunks));
        if (embeddings.Any(value => value is null)) throw new ArgumentException("Embedding collection contains a null item.", nameof(embeddings));

        if (documents.Select(value => value.DocumentId).Distinct(StringComparer.Ordinal).Count() != documents.Count)
            throw new ArgumentException("DocumentId values must be unique.", nameof(documents));
        if (chunks.Select(value => value.ChunkId).Distinct(StringComparer.Ordinal).Count() != chunks.Count)
            throw new ArgumentException("ChunkId values must be unique.", nameof(chunks));
        if (chunks.Select(value => (value.DocumentId, value.Ordinal)).Distinct().Count() != chunks.Count)
            throw new ArgumentException("(DocumentId, Ordinal) pairs must be unique.", nameof(chunks));

        var documentIds = documents.Select(value => value.DocumentId).ToHashSet(StringComparer.Ordinal);
        var documentIdsWithChunks = chunks.Select(value => value.DocumentId).ToHashSet(StringComparer.Ordinal);
        if (documents.Any(document => !documentIdsWithChunks.Contains(document.DocumentId)))
            throw new ArgumentException("Every document must have at least one chunk.", nameof(chunks));
        if (chunks.Any(chunk => !documentIds.Contains(chunk.DocumentId)))
            throw new ArgumentException("A chunk references a document that was not supplied.", nameof(chunks));

        var chunksById = chunks.ToDictionary(chunk => chunk.ChunkId, StringComparer.Ordinal);
        var embeddingChunkIds = new HashSet<string>(StringComparer.Ordinal);
        EmbeddingProfile? commonProfile = null;
        foreach (var embedding in embeddings)
        {
            if (!chunksById.TryGetValue(embedding.Chunk.ChunkId, out var matchingChunk))
                throw new ArgumentException("An embedding references a chunk that was not supplied.", nameof(embeddings));
            if (!embeddingChunkIds.Add(embedding.Chunk.ChunkId))
                throw new ArgumentException("Exactly one embedding per chunk is required.", nameof(embeddings));
            if (!ChunksMatch(matchingChunk, embedding.Chunk))
                throw new ArgumentException("Embedding chunk data does not match the supplied chunk.", nameof(embeddings));
            commonProfile ??= embedding.Profile;
            if (!SameProfile(commonProfile, embedding.Profile))
                throw new ArgumentException("All embeddings in one upsert must use the same embedding profile.", nameof(embeddings));
            if (embedding.Profile.Dimension > 2000)
                throw new ArgumentException("Embedding profile dimension cannot exceed 2000.", nameof(embeddings));
            if (embedding.Vector.Count != embedding.Profile.Dimension)
                throw new ArgumentException("Embedding vector dimension does not match its profile.", nameof(embeddings));
            if (embedding.Vector.Any(value => !float.IsFinite(value)))
                throw new ArgumentException("Embedding vectors must contain only finite values.", nameof(embeddings));
        }
        if (embeddingChunkIds.Count != chunks.Count)
            throw new ArgumentException("Exactly one embedding per chunk is required.", nameof(embeddings));
        return commonProfile!;
    }

    private async Task UpsertProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        EmbeddingProfile profile, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO rag.embedding_profiles (profile_key, provider, model_name, dimension, multilingual)
            VALUES (@profile_key, @provider, @model_name, @dimension, @multilingual)
            ON CONFLICT (profile_key) DO UPDATE SET
                provider = EXCLUDED.provider,
                model_name = EXCLUDED.model_name,
                dimension = EXCLUDED.dimension,
                multilingual = EXCLUDED.multilingual,
                updated_at = now();
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("profile_key", EmbeddingProfileKey.Create(profile));
        command.Parameters.AddWithValue("provider", profile.Provider.Trim());
        command.Parameters.AddWithValue("model_name", profile.ModelName.Trim());
        command.Parameters.AddWithValue("dimension", profile.Dimension);
        command.Parameters.AddWithValue("multilingual", profile.Multilingual);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertDocumentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ProcessedDocument document, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO rag.documents
                (document_id, canonical_url, title, unity_version, content, content_hash, retrieved_at, metadata)
            VALUES (@document_id, @canonical_url, @title, @unity_version, @content, @content_hash, @retrieved_at, @metadata)
            ON CONFLICT (document_id) DO UPDATE SET
                canonical_url = EXCLUDED.canonical_url,
                title = EXCLUDED.title,
                unity_version = EXCLUDED.unity_version,
                content = EXCLUDED.content,
                content_hash = EXCLUDED.content_hash,
                retrieved_at = EXCLUDED.retrieved_at,
                metadata = EXCLUDED.metadata,
                updated_at = now();
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("document_id", document.DocumentId);
        command.Parameters.AddWithValue("canonical_url", document.CanonicalUrl.AbsoluteUri);
        command.Parameters.AddWithValue("title", document.Title);
        command.Parameters.AddWithValue("unity_version", document.UnityVersion);
        command.Parameters.AddWithValue("content", document.Content);
        command.Parameters.AddWithValue("content_hash", document.ContentHash);
        command.Parameters.AddWithValue("retrieved_at", document.RetrievedAt.UtcDateTime);
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(document.Metadata)
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertChunkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DocumentChunk chunk, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO rag.chunks (chunk_id, document_id, section, ordinal, approximate_token_count, content)
            VALUES (@chunk_id, @document_id, @section, @ordinal, @approximate_token_count, @content)
            ON CONFLICT (chunk_id) DO UPDATE SET
                document_id = EXCLUDED.document_id,
                section = EXCLUDED.section,
                ordinal = EXCLUDED.ordinal,
                approximate_token_count = EXCLUDED.approximate_token_count,
                content = EXCLUDED.content,
                updated_at = now();
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("chunk_id", chunk.ChunkId);
        command.Parameters.AddWithValue("document_id", chunk.DocumentId);
        command.Parameters.Add(new NpgsqlParameter("section", NpgsqlDbType.Text) { Value = (object?)chunk.Section ?? DBNull.Value });
        command.Parameters.AddWithValue("ordinal", chunk.Ordinal);
        command.Parameters.AddWithValue("approximate_token_count", chunk.ApproximateTokenCount);
        command.Parameters.AddWithValue("content", chunk.Text);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteStaleChunksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string documentId, string[] currentChunkIds, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM rag.chunks
            WHERE document_id = @document_id
              AND NOT (chunk_id = ANY(@current_chunk_ids));
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.Add(new NpgsqlParameter("current_chunk_ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = currentChunkIds });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertEmbeddingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ChunkEmbedding embedding, EmbeddingProfile profile, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO rag.chunk_embeddings (chunk_id, profile_key, dimension, embedding)
            VALUES (@chunk_id, @profile_key, @dimension, @embedding)
            ON CONFLICT (chunk_id, profile_key) DO UPDATE SET
                dimension = EXCLUDED.dimension,
                embedding = EXCLUDED.embedding,
                embedded_at = now();
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("chunk_id", embedding.Chunk.ChunkId);
        command.Parameters.AddWithValue("profile_key", EmbeddingProfileKey.Create(profile));
        command.Parameters.AddWithValue("dimension", profile.Dimension);
        command.Parameters.AddWithValue("embedding", new Vector(embedding.Vector.ToArray()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = _options.CommandTimeoutSeconds };
        return command;
    }

    private static bool ChunksMatch(DocumentChunk left, DocumentChunk right) =>
        string.Equals(left.ChunkId, right.ChunkId, StringComparison.Ordinal) &&
        string.Equals(left.DocumentId, right.DocumentId, StringComparison.Ordinal) &&
        string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
        string.Equals(left.Section, right.Section, StringComparison.Ordinal) &&
        left.Ordinal == right.Ordinal && left.ApproximateTokenCount == right.ApproximateTokenCount;

    private static bool SameProfile(EmbeddingProfile left, EmbeddingProfile right) =>
        string.Equals(left.Provider.Trim(), right.Provider.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ModelName.Trim(), right.ModelName.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.Dimension == right.Dimension && left.Multilingual == right.Multilingual;
}
