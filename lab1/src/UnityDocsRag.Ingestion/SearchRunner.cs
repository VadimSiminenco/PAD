using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Ingestion;

public sealed class SearchRunner
{
    private readonly ISemanticSearchService _searchService;
    private readonly EmbeddingProfile _profile;
    private readonly ILogger<SearchRunner> _logger;

    public SearchRunner(ISemanticSearchService searchService, EmbeddingProfile profile, ILogger<SearchRunner> logger)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RunAsync(string question, int topK, double similarityThreshold,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question must not be empty or whitespace.", nameof(question));
        cancellationToken.ThrowIfCancellationRequested();

        var query = new RetrievalQuery(question, topK, similarityThreshold);
        var results = await _searchService.SearchAsync(query, _profile, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Semantic search returned {ResultCount} result(s).", results.Count);
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            _logger.LogInformation("Search result {Rank}: {DocumentTitle}; section {Section}; source {CanonicalUrl}; similarity {SimilarityScore:F4}.",
                index + 1, result.SourceTitle, result.Chunk.Section ?? "(none)", result.SourceUrl, result.SimilarityScore);
        }
        return results;
    }
}
