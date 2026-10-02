using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Core.Abstractions;

public interface ILanguageDetector
{
    Task<SupportedLanguage?> DetectAsync(UserQuestion question, CancellationToken cancellationToken);
}

public interface IUnityTopicChecker
{
    Task<bool> IsUnityRelatedAsync(UserQuestion question, CancellationToken cancellationToken);
}

public interface IRetriever
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken);
}

public interface IReranker
{
    Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        UserQuestion question,
        IReadOnlyList<RetrievedChunk> candidates,
        CancellationToken cancellationToken);
}

public interface IContextFilter
{
    Task<IReadOnlyList<RetrievedChunk>> FilterAsync(
        UserQuestion question,
        IReadOnlyList<RetrievedChunk> candidates,
        CancellationToken cancellationToken);
}

public interface IAnswerGenerator
{
    Task<RagAnswer> GenerateAsync(
        UserQuestion question,
        SupportedLanguage language,
        IReadOnlyList<RetrievedChunk> evidence,
        CancellationToken cancellationToken);
}

public interface IRagQueryService
{
    Task<RagAnswer> AnswerAsync(UserQuestion question, CancellationToken cancellationToken);
}
