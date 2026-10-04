using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using Microsoft.Extensions.Logging;

namespace UnityDocsRag.Infrastructure.Query;

public sealed class RagQueryService : IRagQueryService
{
    private readonly ILanguageDetector _languageDetector;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly IReranker _reranker;
    private readonly IAnswerGenerator _answerGenerator;
    private readonly ILogger<RagQueryService> _logger;
    private readonly EmbeddingProfile _profile;
    private readonly int _topK;
    private readonly double _domainSimilarityThreshold;
    private readonly double _evidenceSimilarityThreshold;
    private readonly int _maxEvidenceChunks;

    public RagQueryService(
        ILanguageDetector languageDetector,
        ISemanticSearchService semanticSearch,
        IReranker reranker,
        IAnswerGenerator answerGenerator,
        ILogger<RagQueryService> logger,
        EmbeddingProfile profile,
        int topK,
        double domainSimilarityThreshold,
        double evidenceSimilarityThreshold,
        int maxEvidenceChunks)
    {
        _languageDetector = languageDetector ?? throw new ArgumentNullException(nameof(languageDetector));
        _semanticSearch = semanticSearch ?? throw new ArgumentNullException(nameof(semanticSearch));
        _reranker = reranker ?? throw new ArgumentNullException(nameof(reranker));
        _answerGenerator = answerGenerator ?? throw new ArgumentNullException(nameof(answerGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            _logger.LogInformation(
                "Semantic search candidate {SearchRank}: {SourceTitle} | Section {Section} | SimilarityScore {SimilarityScore}",
                index + 1, candidate.SourceTitle, DisplaySection(candidate.Chunk.Section), candidate.SimilarityScore);
        }

        if (candidates.Count == 0 || candidates.Max(candidate => candidate.SimilarityScore) < _domainSimilarityThreshold)
            return OutOfDomain(language.Value);

        if (candidates.Max(candidate => candidate.SimilarityScore) < _evidenceSimilarityThreshold)
            return InsufficientEvidence(language.Value);

        var eligibleCandidates = candidates
            .Where(candidate => candidate.SimilarityScore >= _evidenceSimilarityThreshold)
            .ToArray();
        if (eligibleCandidates.Length == 0)
            throw new InvalidDataException("Evidence gate passed but no eligible candidates were found.");

        cancellationToken.ThrowIfCancellationRequested();
        var rerankedCandidates = await _reranker.RerankAsync(question, eligibleCandidates, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (rerankedCandidates is null) throw new InvalidDataException("Reranker returned a null result collection.");

        foreach (var candidate in rerankedCandidates)
        {
            _logger.LogInformation(
                "Reranked candidate {FinalRank}: {SourceTitle} | Section {Section} | InitialRank {InitialRank} | SimilarityScore {SimilarityScore} | RerankerScore {RerankerScore}",
                candidate.FinalRank, candidate.SourceTitle, DisplaySection(candidate.Chunk.Section), candidate.InitialRank,
                candidate.SimilarityScore, candidate.RerankerScore);
        }

        var evidence = rerankedCandidates.Take(_maxEvidenceChunks).ToArray();
        for (var index = 0; index < evidence.Length; index++)
        {
            var candidate = evidence[index];
            _logger.LogInformation("Selected evidence {EvidencePosition}: {SourceTitle} | Section {Section}",
                index + 1, candidate.SourceTitle, DisplaySection(candidate.Chunk.Section));
        }
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

    private static string DisplaySection(string? section) =>
        string.IsNullOrWhiteSpace(section) ? "(none)" : section;
}
