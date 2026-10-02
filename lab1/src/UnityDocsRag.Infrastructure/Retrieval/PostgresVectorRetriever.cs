using Npgsql;
using NpgsqlTypes;
using Pgvector;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Storage;

namespace UnityDocsRag.Infrastructure.Retrieval;

/// <summary>Retrieves nearest chunks using a caller-supplied query embedding.</summary>
public sealed class PostgresVectorRetriever : IRetriever
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresVectorRetrieverOptions _options;

    /// <remarks>The caller owns and disposes <paramref name="dataSource"/>.</remarks>
    public PostgresVectorRetriever(NpgsqlDataSource dataSource, PostgresVectorRetrieverOptions? options = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? new PostgresVectorRetrieverOptions();
        _options.Validate();
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(RetrievalQuery query, QueryEmbedding queryEmbedding,
        CancellationToken cancellationToken)
    {
        ValidateInput(query, queryEmbedding);
        cancellationToken.ThrowIfCancellationRequested();

        var profileKey = EmbeddingProfileKey.Create(queryEmbedding.Profile);
        var dimension = queryEmbedding.Profile.Dimension;
        // Dimension has been range-checked below; it is the only interpolated value, to match pgvector's HNSW cast expression.
        var distanceExpression = $"e.embedding::vector({dimension}) <=> @query_vector";
        var sql = $"""
            SELECT c.chunk_id, c.document_id, c.content, c.section, c.ordinal, c.approximate_token_count,
                   d.canonical_url, d.title,
                   1 - ({distanceExpression}) AS similarity
            FROM rag.chunk_embeddings AS e
            INNER JOIN rag.chunks AS c ON c.chunk_id = e.chunk_id
            INNER JOIN rag.documents AS d ON d.document_id = c.document_id
            WHERE e.profile_key = @profile_key
              AND e.dimension = @dimension
              AND (@similarity_threshold IS NULL OR 1 - ({distanceExpression}) >= @similarity_threshold)
            ORDER BY {distanceExpression} ASC, e.chunk_id ASC
            LIMIT @top_k;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue("profile_key", profileKey);
        command.Parameters.AddWithValue("dimension", dimension);
        command.Parameters.AddWithValue("query_vector", new Vector(queryEmbedding.Vector.ToArray()));
        command.Parameters.Add(new NpgsqlParameter("similarity_threshold", NpgsqlDbType.Double)
        {
            Value = (object?)query.SimilarityThreshold ?? DBNull.Value
        });
        command.Parameters.AddWithValue("top_k", query.TopK);

        var results = new List<RetrievedChunk>(Math.Min(query.TopK, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceUrlText = reader.GetString(6);
            if (!Uri.TryCreate(sourceUrlText, UriKind.Absolute, out var sourceUrl))
                throw new InvalidDataException("A retrieved document has an invalid canonical URL.");
            var section = reader.IsDBNull(3) ? null : reader.GetString(3);
            var chunk = new DocumentChunk(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), section,
                reader.GetInt32(4), reader.GetInt32(5));
            var similarity = reader.GetDouble(8);
            results.Add(new RetrievedChunk(chunk, sourceUrl, reader.GetString(7), similarity, results.Count + 1));
        }

        return results.AsReadOnly();
    }

    private static void ValidateInput(RetrievalQuery? query, QueryEmbedding? queryEmbedding)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        if (string.IsNullOrWhiteSpace(query.Text)) throw new ArgumentException("Query text must not be empty.", nameof(query));
        if (query.TopK <= 0) throw new ArgumentOutOfRangeException(nameof(query), "Top-K must be positive.");
        if (query.SimilarityThreshold is { } threshold && (!double.IsFinite(threshold) || threshold < 0 || threshold > 1))
            throw new ArgumentOutOfRangeException(nameof(query), "Similarity threshold must be finite and between 0 and 1.");

        var profile = queryEmbedding.Profile ?? throw new ArgumentException("Query embedding profile must not be null.", nameof(queryEmbedding));
        if (profile.Dimension is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(queryEmbedding), "Embedding dimension must be between 1 and 2000.");
        var profileKey = EmbeddingProfileKey.Create(profile);
        if (string.IsNullOrWhiteSpace(profileKey))
            throw new ArgumentException("Embedding profile key must not be empty.", nameof(queryEmbedding));
        if (queryEmbedding.Vector is null || queryEmbedding.Vector.Count == 0)
            throw new ArgumentException("Query embedding vector must not be empty.", nameof(queryEmbedding));
        if (queryEmbedding.Vector.Count != profile.Dimension)
            throw new ArgumentException("Query embedding dimension does not match its profile.", nameof(queryEmbedding));
        if (queryEmbedding.Vector.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Query embedding must contain only finite values.", nameof(queryEmbedding));
    }
}
