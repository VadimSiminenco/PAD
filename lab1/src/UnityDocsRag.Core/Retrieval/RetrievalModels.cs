using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Core.Retrieval;

public sealed record RetrievalQuery
{
    public RetrievalQuery(string text, int topK, double? similarityThreshold = null)
    {
        Text = string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException("Query text must not be empty.", nameof(text))
            : text;
        if (topK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), "Top-K must be positive.");
        }

        if (similarityThreshold is < 0 or > 1 || (similarityThreshold.HasValue && double.IsNaN(similarityThreshold.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "Similarity threshold must be between 0 and 1.");
        }

        TopK = topK;
        SimilarityThreshold = similarityThreshold;
    }

    public string Text { get; }
    public int TopK { get; }
    public double? SimilarityThreshold { get; }
}

public sealed record RetrievedChunk
{
    public RetrievedChunk(
        DocumentChunk chunk,
        Uri sourceUrl,
        string sourceTitle,
        double similarityScore,
        int initialRank,
        double? rerankerScore = null,
        int? finalRank = null)
    {
        Chunk = chunk ?? throw new ArgumentNullException(nameof(chunk));
        SourceUrl = sourceUrl is { IsAbsoluteUri: true }
            ? sourceUrl
            : throw new ArgumentException("Source URL must be absolute.", nameof(sourceUrl));
        SourceTitle = string.IsNullOrWhiteSpace(sourceTitle)
            ? throw new ArgumentException("Source title must not be empty.", nameof(sourceTitle))
            : sourceTitle;
        SimilarityScore = ValidateScore(similarityScore, nameof(similarityScore));
        if (initialRank <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialRank), "Rank values are one-based and must be positive.");
        }

        if (finalRank is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalRank), "Rank values are one-based and must be positive.");
        }

        InitialRank = initialRank;
        RerankerScore = rerankerScore.HasValue ? ValidateScore(rerankerScore.Value, nameof(rerankerScore)) : null;
        FinalRank = finalRank;
    }

    public DocumentChunk Chunk { get; }
    public Uri SourceUrl { get; }
    public string SourceTitle { get; }
    public double SimilarityScore { get; }
    public int InitialRank { get; }
    public double? RerankerScore { get; }
    public int? FinalRank { get; }

    private static double ValidateScore(double score, string parameterName) =>
        double.IsFinite(score)
            ? score
            : throw new ArgumentOutOfRangeException(parameterName, "Score must be finite.");
}
