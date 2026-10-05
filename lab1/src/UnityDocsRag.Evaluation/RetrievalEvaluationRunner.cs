using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Evaluation;

public sealed record RankedRetrievalSource(int Rank, string Url, double Similarity);

public sealed record RetrievalQuestionEvaluation(
    string Id,
    IReadOnlyList<string> ExpectedSourceUrls,
    IReadOnlyList<RankedRetrievalSource> RetrievedSources,
    double HitAt3,
    double HitAt5,
    double HitAt10,
    double HitAt20,
    double ReciprocalRank,
    double SourceCoverageAt3,
    double SourceCoverageAt5,
    double SourceCoverageAt10,
    double SourceCoverageAt20,
    double AllSourcesHitAt3,
    double AllSourcesHitAt5,
    double AllSourcesHitAt10,
    double AllSourcesHitAt20);

public sealed record RetrievalEvaluationSummary(
    int AnsweredQuestionCount,
    double HitRateAt3,
    double HitRateAt5,
    double HitRateAt10,
    double HitRateAt20,
    double MrrAt20,
    double MeanSourceCoverageAt3,
    double MeanSourceCoverageAt5,
    double MeanSourceCoverageAt10,
    double MeanSourceCoverageAt20,
    double MeanAllSourcesHitAt3,
    double MeanAllSourcesHitAt5,
    double MeanAllSourcesHitAt10,
    double MeanAllSourcesHitAt20);

public sealed record RetrievalEvaluationReport(
    int DatasetVersion,
    string UnityVersion,
    EmbeddingProfile EmbeddingProfile,
    IReadOnlyList<RetrievalQuestionEvaluation> Questions,
    RetrievalEvaluationSummary Summary);

public sealed class RetrievalEvaluationRunner
{
    private const int RetrievalTopK = 20;
    private readonly ISemanticSearchService _searchService;

    public RetrievalEvaluationRunner(ISemanticSearchService searchService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
    }

    public async Task<RetrievalEvaluationReport> RunAsync(
        EvaluationDataset dataset,
        EmbeddingProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var questions = new List<RetrievalQuestionEvaluation>();
        var reciprocalRanks = new List<(EvaluationQuestion Question, IReadOnlyList<string> RankedUrls)>();
        foreach (var question in dataset.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (question.ExpectedStatus != AnswerStatus.Answered)
            {
                continue;
            }

            var query = new RetrievalQuery(question.Text, RetrievalTopK, similarityThreshold: 0);
            var retrieved = await _searchService.SearchAsync(query, profile, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Search returned null for question {question.Id}.");
            cancellationToken.ThrowIfCancellationRequested();

            // The search contract is TopK=20; limit a nonconforming implementation before computing MRR@20.
            var topResults = retrieved.Take(RetrievalTopK).ToArray();
            var rankedUrls = topResults.Select(result => result.SourceUrl.AbsoluteUri).ToArray();
            var sources = topResults.Select((result, index) => new RankedRetrievalSource(
                index + 1, result.SourceUrl.AbsoluteUri, result.SimilarityScore)).ToArray();
            questions.Add(new RetrievalQuestionEvaluation(
                question.Id,
                Array.AsReadOnly(question.ExpectedSourceUrls.ToArray()),
                Array.AsReadOnly(sources),
                EvaluationMetrics.HitAtK(question, rankedUrls, 3)!.Value,
                EvaluationMetrics.HitAtK(question, rankedUrls, 5)!.Value,
                EvaluationMetrics.HitAtK(question, rankedUrls, 10)!.Value,
                EvaluationMetrics.HitAtK(question, rankedUrls, 20)!.Value,
                EvaluationMetrics.ReciprocalRank(question, rankedUrls)!.Value,
                EvaluationMetrics.SourceCoverageAtK(question, rankedUrls, 3)!.Value,
                EvaluationMetrics.SourceCoverageAtK(question, rankedUrls, 5)!.Value,
                EvaluationMetrics.SourceCoverageAtK(question, rankedUrls, 10)!.Value,
                EvaluationMetrics.SourceCoverageAtK(question, rankedUrls, 20)!.Value,
                EvaluationMetrics.AllSourcesHitAtK(question, rankedUrls, 3)!.Value,
                EvaluationMetrics.AllSourcesHitAtK(question, rankedUrls, 5)!.Value,
                EvaluationMetrics.AllSourcesHitAtK(question, rankedUrls, 10)!.Value,
                EvaluationMetrics.AllSourcesHitAtK(question, rankedUrls, 20)!.Value));
            reciprocalRanks.Add((question, rankedUrls));
        }

        if (questions.Count == 0)
        {
            throw new InvalidDataException("Evaluation dataset contains no Answered questions.");
        }

        var summary = new RetrievalEvaluationSummary(
            questions.Count,
            questions.Average(question => question.HitAt3),
            questions.Average(question => question.HitAt5),
            questions.Average(question => question.HitAt10),
            questions.Average(question => question.HitAt20),
            EvaluationMetrics.MeanReciprocalRank(reciprocalRanks)!.Value,
            questions.Average(question => question.SourceCoverageAt3),
            questions.Average(question => question.SourceCoverageAt5),
            questions.Average(question => question.SourceCoverageAt10),
            questions.Average(question => question.SourceCoverageAt20),
            questions.Average(question => question.AllSourcesHitAt3),
            questions.Average(question => question.AllSourcesHitAt5),
            questions.Average(question => question.AllSourcesHitAt10),
            questions.Average(question => question.AllSourcesHitAt20));
        return new RetrievalEvaluationReport(dataset.Version, dataset.UnityVersion, profile,
            questions.AsReadOnly(), summary);
    }
}
