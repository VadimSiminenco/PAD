using System.Diagnostics;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Api;

internal static class SafeTraceTags
{
    public static int SourceCount(IEnumerable<RetrievedChunk> chunks) =>
        chunks.Select(item => item.SourceUrl.AbsoluteUri).Distinct(StringComparer.Ordinal).Count();

    public static void Similarities(Activity? activity, IReadOnlyList<RetrievedChunk> chunks)
    {
        if (activity is null) return;
        for (var index = 0; index < chunks.Count; index++)
            activity.SetTag($"rag.similarity_score.{index + 1}", chunks[index].SimilarityScore);
    }

    public static void MarkFailure(Activity? activity)
    {
        if (activity is not null) activity.SetStatus(ActivityStatusCode.Error);
    }
}

public sealed class TracingSemanticSearchService(ISemanticSearchService inner) : ISemanticSearchService
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        RetrievalQuery query, EmbeddingProfile profile, CancellationToken cancellationToken)
    {
        using var activity = LangfuseTelemetry.ActivitySource.StartActivity("rag.semantic_search");
        if (activity is null) return await inner.SearchAsync(query, profile, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        activity.SetTag("rag.embedding_model", profile.ModelName);
        try
        {
            var result = await inner.SearchAsync(query, profile, cancellationToken).ConfigureAwait(false);
            activity.SetTag("rag.candidate_count", result.Count);
            activity.SetTag("rag.source_count", SafeTraceTags.SourceCount(result));
            SafeTraceTags.Similarities(activity, result);
            return result;
        }
        catch { SafeTraceTags.MarkFailure(activity); throw; }
        finally { activity.SetTag("rag.duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
}

public sealed class TracingReranker(IReranker inner, string modelName) : IReranker
{
    public async Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        UserQuestion question, IReadOnlyList<RetrievedChunk> candidates, CancellationToken cancellationToken)
    {
        using var activity = LangfuseTelemetry.ActivitySource.StartActivity("rag.reranking");
        if (activity is null) return await inner.RerankAsync(question, candidates, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        activity.SetTag("rag.reranker_model", modelName);
        activity.SetTag("rag.candidate_count", candidates.Count);
        activity.SetTag("rag.source_count", SafeTraceTags.SourceCount(candidates));
        SafeTraceTags.Similarities(activity, candidates);
        try
        {
            var result = await inner.RerankAsync(question, candidates, cancellationToken).ConfigureAwait(false);
            activity.SetTag("rag.result_count", result.Count);
            return result;
        }
        catch { SafeTraceTags.MarkFailure(activity); throw; }
        finally { activity.SetTag("rag.duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
}

public sealed class TracingAnswerGenerator(IAnswerGenerator inner, string modelName, string? activityName = null) : IAnswerGenerator
{
    public async Task<RagAnswer> GenerateAsync(
        UserQuestion question, SupportedLanguage language, IReadOnlyList<RetrievedChunk> evidence,
        CancellationToken cancellationToken)
    {
        using var activity = LangfuseTelemetry.ActivitySource.StartActivity(activityName ?? "rag.generation");
        if (activity is null) return await inner.GenerateAsync(question, language, evidence, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        activity.SetTag("rag.generation_model", modelName);
        activity.SetTag("langfuse.observation.type", "generation");
        activity.SetTag("langfuse.observation.model.name", modelName);
        activity.SetTag("rag.language", language.ToString());
        activity.SetTag("rag.evidence_count", evidence.Count);
        activity.SetTag("rag.source_count", SafeTraceTags.SourceCount(evidence));
        SafeTraceTags.Similarities(activity, evidence);
        try
        {
            var answer = await inner.GenerateAsync(question, language, evidence, cancellationToken).ConfigureAwait(false);
            activity.SetTag("rag.status", answer.Status.ToString());
            activity.SetTag("rag.citation_count", answer.Citations.Count);
            return answer;
        }
        catch { SafeTraceTags.MarkFailure(activity); throw; }
        finally { activity.SetTag("rag.duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
}
