using System.Text.Json;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Evaluation;

public sealed record AskQuestionEvaluation(
    string Id,
    string ExpectedCategory,
    string ExpectedStatus,
    string ActualStatus,
    bool StatusMatches,
    string ExpectedLanguage,
    string? DetectedLanguage,
    string? AnswerText,
    IReadOnlyList<string> CitationUrls,
    IReadOnlyList<string> ExpectedSourceUrls,
    IReadOnlyList<string> ExpectedFacts);

public sealed record AskCategorySummary(
    string ExpectedCategory,
    int QuestionCount,
    int MatchedStatusCount,
    double StatusMatchRate);

public sealed record AskEvaluationSummary(
    int QuestionCount,
    int MatchedStatusCount,
    double StatusMatchRate,
    IReadOnlyList<AskCategorySummary> ByExpectedCategory);

public sealed record AskEvaluationReport(
    int DatasetVersion,
    string UnityVersion,
    IReadOnlyList<AskQuestionEvaluation> Questions,
    AskEvaluationSummary Summary);

public sealed class AskEvaluationRunner
{
    private readonly IRagQueryService _queryService;

    public AskEvaluationRunner(IRagQueryService queryService)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
    }

    public async Task<AskEvaluationReport> RunAsync(EvaluationDataset dataset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<AskQuestionEvaluation>(dataset.Questions.Count);
        foreach (var question in dataset.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await EvaluateQuestionAsync(question, cancellationToken).ConfigureAwait(false));
        }

        return BuildReport(dataset, results);
    }

    public async Task<AskEvaluationReport> RunResumableAsync(EvaluationDataset dataset,
        string datasetPath, string configPath, string outputPath, Action<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(outputPath))
        {
            throw new IOException("Output file already exists; it will not be overwritten.");
        }

        var store = await AskEvaluationCheckpointStore.CreateAsync(dataset, datasetPath, configPath,
            outputPath, cancellationToken).ConfigureAwait(false);
        var results = (await store.LoadCompletedAsync(cancellationToken).ConfigureAwait(false)).ToList();
        for (var index = 0; index < results.Count; index++)
        {
            ValidateRestoredResult(dataset.Questions[index], results[index]);
        }

        for (var index = results.Count; index < dataset.Questions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await EvaluateQuestionAsync(dataset.Questions[index], cancellationToken).ConfigureAwait(false);
            results.Add(result);
            await store.SaveAsync(results, cancellationToken).ConfigureAwait(false);
            progress?.Invoke($"{result.Id} ({index + 1}/{dataset.Questions.Count}): {result.ActualStatus}");
        }

        return BuildReport(dataset, results);
    }

    private async Task<AskQuestionEvaluation> EvaluateQuestionAsync(EvaluationQuestion question,
        CancellationToken cancellationToken)
    {
        var answer = await _queryService.AnswerAsync(new UserQuestion(question.Text), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Ask returned null for question {question.Id}.");
        cancellationToken.ThrowIfCancellationRequested();

        return new AskQuestionEvaluation(
            question.Id,
            question.Category.ToString(),
            question.ExpectedStatus.ToString(),
            answer.Status.ToString(),
            EvaluationMetrics.StatusMatches(question.ExpectedStatus, answer.Status),
            question.Language,
            LanguageCode(answer.Language),
            answer.Text,
            Array.AsReadOnly(answer.Citations.Select(citation => citation.Url.AbsoluteUri).ToArray()),
            Array.AsReadOnly(question.ExpectedSourceUrls.ToArray()),
            Array.AsReadOnly(question.ExpectedFacts.ToArray()));
    }

    private static void ValidateRestoredResult(EvaluationQuestion question, AskQuestionEvaluation result)
    {
        if (result.Id != question.Id ||
            result.ExpectedCategory != question.Category.ToString() ||
            result.ExpectedStatus != question.ExpectedStatus.ToString() ||
            result.ExpectedLanguage != question.Language ||
            result.ExpectedSourceUrls is null ||
            !result.ExpectedSourceUrls.SequenceEqual(question.ExpectedSourceUrls, StringComparer.Ordinal) ||
            result.ExpectedFacts is null ||
            !result.ExpectedFacts.SequenceEqual(question.ExpectedFacts, StringComparer.Ordinal) ||
            !Enum.TryParse<AnswerStatus>(result.ActualStatus, out var actualStatus) ||
            result.StatusMatches != EvaluationMetrics.StatusMatches(question.ExpectedStatus, actualStatus))
        {
            throw new InvalidDataException("Evaluation checkpoint contains a result inconsistent with the dataset.");
        }
    }

    private static AskEvaluationReport BuildReport(EvaluationDataset dataset,
        List<AskQuestionEvaluation> results)
    {
        var categorySummaries = results
            .GroupBy(result => result.ExpectedCategory, StringComparer.Ordinal)
            .Select(group => new AskCategorySummary(
                group.Key,
                group.Count(),
                group.Count(result => result.StatusMatches),
                (double)group.Count(result => result.StatusMatches) / group.Count()))
            .ToArray();
        var matchedCount = results.Count(result => result.StatusMatches);
        var summary = new AskEvaluationSummary(
            results.Count,
            matchedCount,
            (double)matchedCount / results.Count,
            Array.AsReadOnly(categorySummaries));
        return new AskEvaluationReport(dataset.Version, dataset.UnityVersion,
            results.AsReadOnly(), summary);
    }

    private static string? LanguageCode(SupportedLanguage? language) => language switch
    {
        SupportedLanguage.Russian => "ru",
        SupportedLanguage.English => "en",
        null => null,
        _ => throw new InvalidDataException("Ask returned an unsupported language value.")
    };
}

public static class AskEvaluationReportWriter
{
    public static async Task WriteNewAsync(AskEvaluationReport report, string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(path))
        {
            throw new IOException("Output file already exists; it will not be overwritten.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
