using System.Diagnostics;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Api;

public sealed record ModelComparisonRun(
    string Model,
    IAnswerGenerator Generator);

public sealed record ModelComparisonAnswer(
    string Model,
    string Status,
    string? Text,
    IReadOnlyList<AskCitationResponse> Citations,
    double? GenerationSeconds,
    string? SafeError);

public sealed record ModelComparisonResponse(
    string Status,
    string? Language,
    bool IsComparison,
    IReadOnlyList<ModelComparisonAnswer> Models,
    string? Message,
    double? OverallSeconds,
    string? FasterModel,
    double? FasterRatio);

/// <summary>Runs shared retrieval once, then model generations serially.</summary>
public sealed class ModelComparisonService(
    ILanguageDetector languageDetector,
    ISemanticSearchService semanticSearch,
    IReranker reranker,
    EmbeddingProfile profile,
    int topK,
    double domainSimilarityThreshold,
    double evidenceSimilarityThreshold,
    int maxEvidenceChunks,
    IReadOnlyList<ModelComparisonRun> runs)
{
    public async Task<ModelComparisonResponse> CompareAsync(UserQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        cancellationToken.ThrowIfCancellationRequested();
        var overallStarted = Stopwatch.GetTimestamp();
        var language = await languageDetector.DetectAsync(question, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (language is null)
            return Refusal(AnswerStatus.UnsupportedLanguage, null,
                "Поддерживаются только русский и английский языки. Only Russian and English are supported.");

        var candidates = await semanticSearch.SearchAsync(new RetrievalQuery(question.Text, topK), profile, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates is null) throw new InvalidDataException("Semantic search returned a null result collection.");
        if (candidates.Count == 0 || candidates.Max(candidate => candidate.SimilarityScore) < domainSimilarityThreshold)
            return Refusal(AnswerStatus.OutOfDomain, language, language == SupportedLanguage.Russian
                ? "Поддерживаются только вопросы по Unity." : "Only Unity-related questions are supported.");
        if (candidates.Max(candidate => candidate.SimilarityScore) < evidenceSimilarityThreshold)
            return Refusal(AnswerStatus.InsufficientEvidence, language, language == SupportedLanguage.Russian
                ? "В найденной документации недостаточно информации для ответа."
                : "The retrieved documentation does not contain enough information to answer.");

        var eligible = candidates.Where(candidate => candidate.SimilarityScore >= evidenceSimilarityThreshold).ToArray();
        if (eligible.Length == 0) throw new InvalidDataException("Evidence gate passed but no eligible candidates were found.");
        cancellationToken.ThrowIfCancellationRequested();
        var reranked = await reranker.RerankAsync(question, eligible, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (reranked is null) throw new InvalidDataException("Reranker returned a null result collection.");
        var evidence = Array.AsReadOnly(reranked.Take(maxEvidenceChunks).ToArray());

        var answers = new List<ModelComparisonAnswer>(runs.Count);
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            try
            {
                var answer = await run.Generator.GenerateAsync(question, language.Value, evidence, cancellationToken)
                    .ConfigureAwait(false);
                var elapsed = Math.Max(0d, Stopwatch.GetElapsedTime(started).TotalSeconds);
                if (answer.Status == AnswerStatus.Failed)
                {
                    answers.Add(new ModelComparisonAnswer(run.Model, "Failed", null,
                        Array.Empty<AskCitationResponse>(), elapsed,
                        "Не удалось получить ответ этой модели. Проверьте, что модель установлена в Ollama."));
                    continue;
                }
                answers.Add(new ModelComparisonAnswer(run.Model, answer.Status.ToString(), answer.Text,
                    answer.Citations.Select(c => new AskCitationResponse(c.Title, c.Section, c.Url.AbsoluteUri)).ToArray(),
                    elapsed, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                var elapsed = Math.Max(0d, Stopwatch.GetElapsedTime(started).TotalSeconds);
                answers.Add(new ModelComparisonAnswer(run.Model, "Error", null, Array.Empty<AskCitationResponse>(),
                    elapsed, "Не удалось получить ответ этой модели. Проверьте, что модель установлена в Ollama."));
            }
        }

        var faster = answers.Count == 2 && answers.All(answer => answer.GenerationSeconds.HasValue && answer.Status is not ("Error" or "Failed"))
            ? answers.OrderBy(answer => answer.GenerationSeconds).First().Model : null;
        double? ratio = answers.Count == 2 && answers.All(answer => answer.GenerationSeconds is > 0 && answer.Status is not ("Error" or "Failed"))
            ? answers.Max(answer => answer.GenerationSeconds!.Value) / answers.Min(answer => answer.GenerationSeconds!.Value)
            : null;
        return new ModelComparisonResponse("Completed", language.Value.ToString(), true, answers, null,
            Math.Max(0d, Stopwatch.GetElapsedTime(overallStarted).TotalSeconds), faster, ratio);
    }

    private static ModelComparisonResponse Refusal(AnswerStatus status, SupportedLanguage? language, string message) =>
        new(status.ToString(), language?.ToString(), false, Array.Empty<ModelComparisonAnswer>(), message, null, null, null);
}
