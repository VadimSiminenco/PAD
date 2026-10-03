using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Infrastructure.Query;

public sealed class RagQueryService : IRagQueryService
{
    private readonly ILanguageDetector _languageDetector;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly IAnswerGenerator _answerGenerator;
    private readonly EmbeddingProfile _profile;
    private readonly int _topK;
    private readonly double _domainSimilarityThreshold;
    private readonly double _evidenceSimilarityThreshold;
    private readonly int _maxEvidenceChunks;

    public RagQueryService(
        ILanguageDetector languageDetector,
        ISemanticSearchService semanticSearch,
        IAnswerGenerator answerGenerator,
        EmbeddingProfile profile,
        int topK,
        double domainSimilarityThreshold,
        double evidenceSimilarityThreshold,
        int maxEvidenceChunks)
    {
        _languageDetector = languageDetector ?? throw new ArgumentNullException(nameof(languageDetector));
        _semanticSearch = semanticSearch ?? throw new ArgumentNullException(nameof(semanticSearch));
        _answerGenerator = answerGenerator ?? throw new ArgumentNullException(nameof(answerGenerator));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK), "TopK must be positive.");
        if (maxEvidenceChunks <= 0 || maxEvidenceChunks > topK)
            throw new ArgumentOutOfRangeException(nameof(maxEvidenceChunks), "MaxEvidenceChunks must be positive and no greater than TopK.");
        if (!double.IsFinite(domainSimilarityThreshold) || domainSimilarityThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(domainSimilarityThreshold), "Domain threshold must be finite and between 0 and 1.");
        if (!double.IsFinite(evidenceSimilarityThreshold) || evidenceSimilarityThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(evidenceSimilarityThreshold), "Evidence threshold must be finite and between 0 and 1.");
        if (domainSimilarityThreshold >= evidenceSimilarityThreshold)
            throw new ArgumentException("Domain threshold must be lower than evidence threshold.", nameof(domainSimilarityThreshold));

        _topK = topK;
        _domainSimilarityThreshold = domainSimilarityThreshold;
        _evidenceSimilarityThreshold = evidenceSimilarityThreshold;
        _maxEvidenceChunks = maxEvidenceChunks;
    }

    public async Task<RagAnswer> AnswerAsync(UserQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        cancellationToken.ThrowIfCancellationRequested();

        var language = await _languageDetector.DetectAsync(question, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (language is null)
        {
            return new RagAnswer(
                "Поддерживаются только русский и английский языки. Only Russian and English are supported.",
                null,
                AnswerStatus.UnsupportedLanguage);
        }

        var query = new RetrievalQuery(question.Text, _topK);
        var candidates = await _semanticSearch.SearchAsync(query, _profile, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates is null) throw new InvalidDataException("Semantic search returned a null result collection.");

        if (candidates.Count == 0 || candidates[0].SimilarityScore < _domainSimilarityThreshold)
            return OutOfDomain(language.Value);

        if (candidates[0].SimilarityScore < _evidenceSimilarityThreshold)
            return InsufficientEvidence(language.Value);

        var evidence = candidates
            .Where(candidate => candidate.SimilarityScore >= _evidenceSimilarityThreshold)
            .Take(_maxEvidenceChunks)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return await _answerGenerator.GenerateAsync(question, language.Value, evidence, cancellationToken)
            .ConfigureAwait(false);
    }

    private static RagAnswer OutOfDomain(SupportedLanguage language) => new(
        language == SupportedLanguage.Russian
            ? "Поддерживаются только вопросы по Unity."
            : "Only Unity-related questions are supported.",
        language,
        AnswerStatus.OutOfDomain);

    private static RagAnswer InsufficientEvidence(SupportedLanguage language) => new(
        language == SupportedLanguage.Russian
            ? "В найденной документации недостаточно информации для ответа."
            : "The retrieved documentation does not contain enough information to answer.",
        language,
        AnswerStatus.InsufficientEvidence);
}
