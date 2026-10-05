using UnityDocsRag.Core.Generation;
using UnityDocsRag.Evaluation;

namespace UnityDocsRag.Tests.Evaluation;

public sealed class EvaluationMetricsTests
{
    private static readonly EvaluationDataset Dataset = EvaluationDataset.Parse("""
        {
          "Version": 1,
          "UnityVersion": "6000.3",
          "Questions": [
            {
              "Id": "A", "Language": "en", "Category": "InCorpus",
              "Text": "How does this API work?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": true,
              "ExpectedSourceUrls": ["https://example.test/a", "https://example.test/b"],
              "ExpectedFacts": ["A documented fact"]
            },
            {
              "Id": "N", "Language": "ru", "Category": "UnityWithoutCorpusEvidence",
              "Text": "Вопрос без ответа?", "ExpectedStatus": "InsufficientEvidence",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            },
            {
              "Id": "S", "Language": "en", "Category": "InCorpus",
              "Text": "Single-source question?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": ["https://example.test/single"],
              "ExpectedFacts": ["A single-source fact"]
            }
          ]
        }
        """);

    private static EvaluationQuestion Answered => Dataset.Questions[0];
    private static EvaluationQuestion Negative => Dataset.Questions[1];
    private static EvaluationQuestion SingleSource => Dataset.Questions[2];

    [Fact]
    public void AnyExpectedUrlCountsAsHitAtK()
    {
        string[] ranked = ["https://example.test/other", "https://example.test/b"];

        Assert.Equal(0.0, EvaluationMetrics.HitAtK(Answered, ranked, 1));
        Assert.Equal(1.0, EvaluationMetrics.HitAtK(Answered, ranked, 2));
        Assert.Equal(0.5, EvaluationMetrics.ReciprocalRank(Answered, ranked));
    }

    [Fact]
    public void MissingExpectedUrlsProduceZero()
    {
        string[] ranked = ["https://example.test/other"];

        Assert.Equal(0.0, EvaluationMetrics.HitAtK(Answered, ranked, 5));
        Assert.Equal(0.0, EvaluationMetrics.ReciprocalRank(Answered, ranked));
    }

    [Fact]
    public void RepeatedChunkUrlsKeepTheirOriginalRankPositions()
    {
        string[] ranked = [
            "https://example.test/other",
            "https://example.test/other",
            "https://example.test/b",
            "https://example.test/b"
        ];

        Assert.Equal(0.0, EvaluationMetrics.HitAtK(Answered, ranked, 2));
        Assert.Equal(1.0, EvaluationMetrics.HitAtK(Answered, ranked, 3));
        Assert.Equal(1.0 / 3.0, EvaluationMetrics.ReciprocalRank(Answered, ranked));
    }

    [Fact]
    public void NegativeQuestionHasNoRetrievalMetric()
    {
        string[] ranked = ["https://example.test/a"];

        Assert.Null(EvaluationMetrics.HitAtK(Negative, ranked, 1));
        Assert.Null(EvaluationMetrics.ReciprocalRank(Negative, ranked));
    }

    [Fact]
    public void MeanReciprocalRankSkipsNegativeQuestionsButIncludesMisses()
    {
        var cases = new (EvaluationQuestion Question, IReadOnlyList<string> RankedUrls)[]
        {
            (Answered, ["https://example.test/other", "https://example.test/a"]),
            (Negative, ["https://example.test/a"]),
            (Answered, ["https://example.test/other"])
        };

        Assert.Equal(0.25, EvaluationMetrics.MeanReciprocalRank(cases));
        Assert.Null(EvaluationMetrics.MeanReciprocalRank(
            new (EvaluationQuestion, IReadOnlyList<string>)[] { (Negative, Array.Empty<string>()) }));
    }

    [Fact]
    public void StatusComparisonIsExactAndAggregateUsesAllCases()
    {
        Assert.True(EvaluationMetrics.StatusMatches(AnswerStatus.Answered, AnswerStatus.Answered));
        Assert.False(EvaluationMetrics.StatusMatches(AnswerStatus.InsufficientEvidence, AnswerStatus.OutOfDomain));

        var cases = new (AnswerStatus Expected, AnswerStatus Actual)[]
        {
            (AnswerStatus.Answered, AnswerStatus.Answered),
            (AnswerStatus.InsufficientEvidence, AnswerStatus.OutOfDomain),
            (AnswerStatus.OutOfDomain, AnswerStatus.OutOfDomain)
        };

        Assert.Equal(2.0 / 3.0, EvaluationMetrics.StatusMatchRate(cases));
        Assert.Null(EvaluationMetrics.StatusMatchRate(Array.Empty<(AnswerStatus, AnswerStatus)>()));
    }

    [Fact]
    public void InvalidKIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationMetrics.HitAtK(Answered, [], 0));
    }

    [Fact]
    public void MultipleExpectedUrlsRequireDistinctMatchesForFullCoverage()
    {
        string[] ranked = ["https://example.test/a", "https://example.test/a", "https://example.test/b"];

        Assert.Equal(0.5, EvaluationMetrics.SourceCoverageAtK(Answered, ranked, 1));
        Assert.Equal(0.5, EvaluationMetrics.SourceCoverageAtK(Answered, ranked, 2));
        Assert.Equal(1.0, EvaluationMetrics.SourceCoverageAtK(Answered, ranked, 3));
        Assert.Equal(0.0, EvaluationMetrics.AllSourcesHitAtK(Answered, ranked, 2));
        Assert.Equal(1.0, EvaluationMetrics.AllSourcesHitAtK(Answered, ranked, 3));
    }

    [Fact]
    public void DuplicateRetrievedUrlDoesNotIncreaseCoverageAndMissIsZero()
    {
        string[] ranked = ["https://example.test/other", "https://example.test/b", "https://example.test/b"];

        Assert.Equal(0.0, EvaluationMetrics.SourceCoverageAtK(Answered, ranked, 1));
        Assert.Equal(0.5, EvaluationMetrics.SourceCoverageAtK(Answered, ranked, 3));
        Assert.Equal(0.0, EvaluationMetrics.AllSourcesHitAtK(Answered, ranked, 3));
        Assert.Equal(0.0, EvaluationMetrics.SourceCoverageAtK(Answered, [], 20));
    }

    [Fact]
    public void SingleExpectedSourceMakesCoverageAndAllSourcesHitEquivalentToHit()
    {
        string[] ranked = ["https://example.test/other", "https://example.test/single"];

        Assert.Equal(0.0, EvaluationMetrics.SourceCoverageAtK(SingleSource, ranked, 1));
        Assert.Equal(0.0, EvaluationMetrics.AllSourcesHitAtK(SingleSource, ranked, 1));
        Assert.Equal(1.0, EvaluationMetrics.SourceCoverageAtK(SingleSource, ranked, 2));
        Assert.Equal(1.0, EvaluationMetrics.AllSourcesHitAtK(SingleSource, ranked, 2));
        Assert.Equal(EvaluationMetrics.HitAtK(SingleSource, ranked, 2),
            EvaluationMetrics.AllSourcesHitAtK(SingleSource, ranked, 2));
    }

    [Fact]
    public void NegativeQuestionsHaveNoCoverageMetricsAndInvalidKIsRejected()
    {
        string[] ranked = ["https://example.test/a"];

        Assert.Null(EvaluationMetrics.SourceCoverageAtK(Negative, ranked, 1));
        Assert.Null(EvaluationMetrics.AllSourcesHitAtK(Negative, ranked, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationMetrics.SourceCoverageAtK(Answered, ranked, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationMetrics.AllSourcesHitAtK(Answered, ranked, 0));
    }
}
