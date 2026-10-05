using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Evaluation;

public static class EvaluationMetrics
{
    public static double? HitAtK(EvaluationQuestion question, IReadOnlyList<string> rankedUrls, int k)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(rankedUrls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (question.ExpectedStatus != AnswerStatus.Answered)
        {
            return null;
        }

        var expected = new HashSet<string>(question.ExpectedSourceUrls, StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(k, rankedUrls.Count); index++)
        {
            if (expected.Contains(rankedUrls[index]))
            {
                return 1.0;
            }
        }

        return 0.0;
    }

    public static double? SourceCoverageAtK(EvaluationQuestion question, IReadOnlyList<string> rankedUrls, int k)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(rankedUrls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (question.ExpectedStatus != AnswerStatus.Answered)
        {
            return null;
        }

        var expected = new HashSet<string>(question.ExpectedSourceUrls, StringComparer.Ordinal);
        if (expected.Count == 0)
        {
            throw new InvalidDataException("Answered question must have expected source URLs.");
        }

        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(k, rankedUrls.Count); index++)
        {
            remaining.Remove(rankedUrls[index]);
        }

        return (double)(expected.Count - remaining.Count) / expected.Count;
    }

    public static double? AllSourcesHitAtK(EvaluationQuestion question, IReadOnlyList<string> rankedUrls, int k)
    {
        var coverage = SourceCoverageAtK(question, rankedUrls, k);
        return coverage is null ? null : coverage == 1.0 ? 1.0 : 0.0;
    }

    public static double? ReciprocalRank(EvaluationQuestion question, IReadOnlyList<string> rankedUrls)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(rankedUrls);

        if (question.ExpectedStatus != AnswerStatus.Answered)
        {
            return null;
        }

        var expected = new HashSet<string>(question.ExpectedSourceUrls, StringComparer.Ordinal);
        for (var index = 0; index < rankedUrls.Count; index++)
        {
            if (expected.Contains(rankedUrls[index]))
            {
                return 1.0 / (index + 1);
            }
        }

        return 0.0;
    }

    public static double? MeanReciprocalRank(
        IEnumerable<(EvaluationQuestion Question, IReadOnlyList<string> RankedUrls)> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var sum = 0.0;
        var count = 0;
        foreach (var (question, rankedUrls) in results)
        {
            var rank = ReciprocalRank(question, rankedUrls);
            if (rank is null)
            {
                continue;
            }

            sum += rank.Value;
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    public static bool StatusMatches(AnswerStatus expected, AnswerStatus actual) => expected == actual;

    public static double? StatusMatchRate(IEnumerable<(AnswerStatus Expected, AnswerStatus Actual)> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var matches = 0;
        var count = 0;
        foreach (var (expected, actual) in results)
        {
            if (StatusMatches(expected, actual))
            {
                matches++;
            }

            count++;
        }

        return count == 0 ? null : (double)matches / count;
    }
}
