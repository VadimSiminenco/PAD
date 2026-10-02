using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;

namespace UnityDocsRag.Core.Abstractions;

public interface IDocumentSource
{
    IAsyncEnumerable<RetrievedDocument> GetDocumentsAsync(CancellationToken cancellationToken);
}

public interface IDocumentPreprocessor
{
    Task<ProcessedDocument> ProcessAsync(RetrievedDocument document, CancellationToken cancellationToken);
}

public interface IDocumentChunker
{
    Task<IReadOnlyList<DocumentChunk>> ChunkAsync(ProcessedDocument document, CancellationToken cancellationToken);
}

public interface IEmbeddingProvider
{
    Task<IReadOnlyList<ChunkEmbedding>> EmbedAsync(
        IReadOnlyList<DocumentChunk> chunks,
        EmbeddingProfile profile,
        CancellationToken cancellationToken);
}

public interface IVectorStore
{
    Task UpsertAsync(
        IReadOnlyList<ProcessedDocument> documents,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<ChunkEmbedding> embeddings,
        CancellationToken cancellationToken);
}
