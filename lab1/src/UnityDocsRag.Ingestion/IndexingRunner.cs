using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Preprocessing;
using UnityDocsRag.Infrastructure.Storage;

namespace UnityDocsRag.Ingestion;

public sealed class IndexingRunner
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<IndexingRunner> _logger;

    public IndexingRunner(IEmbeddingProvider embeddingProvider, IVectorStore vectorStore, ILogger<IndexingRunner> logger)
    {
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IndexingRunSummary> RunAsync(ProcessingArtifactSnapshot snapshot, EmbeddingProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Documents is null || snapshot.Documents.Count == 0)
            throw new ArgumentException("Snapshot must contain at least one document.", nameof(snapshot));
        if (snapshot.Chunks is null || snapshot.Chunks.Count == 0)
            throw new ArgumentException("Snapshot must contain at least one chunk.", nameof(snapshot));
        if (profile.Dimension <= 0 || profile.Dimension > 2000)
            throw new ArgumentException("Embedding profile dimension must be between 1 and 2000.", nameof(profile));

        var embeddings = await _embeddingProvider.EmbedAsync(snapshot.Chunks, profile, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (embeddings is null || embeddings.Count != snapshot.Chunks.Count)
            throw new InvalidDataException("Embedding provider returned an unexpected number of embeddings.");
        for (var index = 0; index < embeddings.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = embeddings[index];
            if (embedding is null || !string.Equals(embedding.Chunk.ChunkId, snapshot.Chunks[index].ChunkId, StringComparison.Ordinal))
                throw new InvalidDataException("Embedding provider returned embeddings in an unexpected chunk order.");
            if (!ProfilesMatch(embedding.Profile, profile))
                throw new InvalidDataException("Embedding provider returned an unexpected embedding profile.");
            if (embedding.Vector.Count != profile.Dimension)
                throw new InvalidDataException("Embedding provider returned a vector with an unexpected dimension.");
        }

        await _vectorStore.UpsertAsync(snapshot.Documents, snapshot.Chunks, embeddings, cancellationToken).ConfigureAwait(false);
        var summary = new IndexingRunSummary(snapshot.Documents.Count, snapshot.Chunks.Count, embeddings.Count,
            EmbeddingProfileKey.Create(profile), profile.Dimension);
        _logger.LogInformation("Indexing complete: documents {DocumentCount}, chunks {ChunkCount}, embeddings {EmbeddingCount}, profile {ProfileKey}, dimension {Dimension}.",
            summary.DocumentCount, summary.ChunkCount, summary.EmbeddingCount, summary.ProfileKey, summary.Dimension);
        return summary;
    }

    private static bool ProfilesMatch(EmbeddingProfile? left, EmbeddingProfile right) => left is not null &&
        string.Equals(left.Provider.Trim(), right.Provider.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ModelName.Trim(), right.ModelName.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.Dimension == right.Dimension && left.Multilingual == right.Multilingual;
}

public sealed record IndexingRunSummary(int DocumentCount, int ChunkCount, int EmbeddingCount, string ProfileKey, int Dimension);
