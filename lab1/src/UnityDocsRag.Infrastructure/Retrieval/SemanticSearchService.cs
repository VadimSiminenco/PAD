using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Infrastructure.Retrieval;

public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly IQueryEmbeddingProvider _queryEmbeddingProvider;
    private readonly IRetriever _retriever;

    public SemanticSearchService(IQueryEmbeddingProvider queryEmbeddingProvider, IRetriever retriever)
    {
        _queryEmbeddingProvider = queryEmbeddingProvider ?? throw new ArgumentNullException(nameof(queryEmbeddingProvider));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, EmbeddingProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var queryEmbedding = await _queryEmbeddingProvider.EmbedQueryAsync(query.Text, profile, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await _retriever.RetrieveAsync(query, queryEmbedding, cancellationToken).ConfigureAwait(false);
    }
}
