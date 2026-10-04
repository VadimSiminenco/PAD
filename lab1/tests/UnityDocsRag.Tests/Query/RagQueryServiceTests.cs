using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Ingestion;
using UnityDocsRag.Infrastructure.Query;

namespace UnityDocsRag.Tests.Query;

public sealed class RagQueryServiceTests
{
    private static readonly EmbeddingProfile Profile = new("Ollama", "embeddinggemma", 3, true);

    [Fact]
    public async Task UnsupportedLanguageDoesNotSearchRerankOrGenerate()
    {
        var search = new FakeSearch((_, _, _) => throw new InvalidOperationException());
        var reranker = new FakeReranker((_, _, _) => throw new InvalidOperationException());
        var generator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException());
        var service = Service(new FakeDetector((_, _) => Task.FromResult<SupportedLanguage?>(null)), search, reranker, generator);

        var answer = await service.AnswerAsync(new UserQuestion("unsupported"), CancellationToken.None);

        Assert.Equal(AnswerStatus.UnsupportedLanguage, answer.Status);
        Assert.Null(answer.Language);
        Assert.Contains("Russian and English", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, search.Calls);
        Assert.Equal(0, reranker.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task EmptyResultsReturnOutOfDomainWithoutRerankingOrGeneration()
    {
        var reranker = new FakeReranker((_, _, _) => throw new InvalidOperationException());
        var generator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException());
        var service = Service(search: Search(Array.Empty<RetrievedChunk>()), reranker: reranker, generator: generator);

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.OutOfDomain, answer.Status);
        Assert.Equal(SupportedLanguage.English, answer.Language);
        Assert.Equal(0, reranker.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task SimilarityBelowDomainThresholdReturnsOutOfDomainRegardlessOfRerankerMetadata()
    {
        var candidate = Chunk("candidate", 0.249, rerankerScore: 1, finalRank: 1);
        var reranker = new FakeReranker((_, _, _) => throw new InvalidOperationException());
        var generator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException());
        var service = Service(search: Search([candidate]), reranker: reranker, generator: generator);

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.OutOfDomain, answer.Status);
        Assert.Equal(0, reranker.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task SimilarityBetweenThresholdsReturnsInsufficientEvidenceRegardlessOfRerankerMetadata()
    {
        var candidate = Chunk("candidate", 0.3, rerankerScore: 1, finalRank: 1);
        var reranker = new FakeReranker((_, _, _) => throw new InvalidOperationException());
        var generator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException());
        var service = Service(search: Search([candidate]), reranker: reranker, generator: generator);

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.InsufficientEvidence, answer.Status);
        Assert.Equal(0, reranker.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task EvidenceThresholdIsInclusiveAndSearchHasNoSimilarityCutoff()
    {
        var search = new FakeSearch((query, _, _) =>
        {
            Assert.Equal(5, query.TopK);
            Assert.Null(query.SimilarityThreshold);
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>([Chunk("eligible", 0.45)]);
        });
        var reranker = new FakeReranker((_, candidates, _) =>
        {
            Assert.Single(candidates);
            Assert.Equal("eligible", candidates[0].Chunk.ChunkId);
            return Task.FromResult(candidates);
        });
        var generator = new FakeGenerator((_, _, evidence, _) =>
        {
            Assert.Single(evidence);
            return Task.FromResult(Answered());
        });
        var service = Service(search: search, reranker: reranker, generator: generator);

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(1, reranker.Calls);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task FiltersByOriginalSimilarityReranksAllEligibleThenCapsInRerankedOrder()
    {
        var candidates = new[]
        {
            Chunk("weak", 0.44), Chunk("first", 0.91), Chunk("second", 0.8), Chunk("third", 0.7)
        };
        RetrievedChunk[]? generatorEvidence = null;
        var reranker = new FakeReranker((_, eligible, _) =>
        {
            Assert.Equal(new[] { "first", "second", "third" }, eligible.Select(item => item.Chunk.ChunkId));
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>([eligible[2], eligible[0], eligible[1]]);
        });
        var generator = new FakeGenerator((_, _, evidence, _) =>
        {
            generatorEvidence = evidence.ToArray();
            return Task.FromResult(Answered());
        });
        var service = Service(search: Search(candidates), reranker: reranker, generator: generator, maxEvidence: 2);

        await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(1, reranker.Calls);
        Assert.Equal(new[] { "first", "second", "third" }, reranker.ReceivedCandidates!.Select(item => item.Chunk.ChunkId));
        Assert.Equal(new[] { "third", "first" }, generatorEvidence!.Select(item => item.Chunk.ChunkId));
    }

    [Fact]
    public async Task GeneratorReceivesRerankedEvidenceWithoutChangingItsAnswer()
    {
        var candidates = new[] { Chunk("one", 0.8), Chunk("two", 0.75) };
        var expected = Answered("generated unchanged");
        var generator = new FakeGenerator((_, _, evidence, _) =>
        {
            Assert.Equal(new[] { "two", "one" }, evidence.Select(item => item.Chunk.ChunkId));
            return Task.FromResult(expected);
        });
        var reranker = new FakeReranker((_, eligible, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>([eligible[1], eligible[0]]));
        var service = Service(search: Search(candidates), reranker: reranker, generator: generator);

        var actual = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task LogsOnlySafeSearchRerankAndSelectedEvidenceMetadata()
    {
        const string questionText = "QUESTION_MARKER connection=CONNECTION_SECRET password=PASSWORD_SECRET vector=VECTOR_MARKER";
        const string privateChunkText = "CHUNK_TEXT_MARKER vector=VECTOR_MARKER connection=CONNECTION_SECRET password=PASSWORD_SECRET";
        var first = new RetrievedChunk(
            new DocumentChunk("private-chunk-id", "private-document-id", privateChunkText, "Movement API", 0, 1),
            new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/a.html"), "API Alpha", 0.82, 4);
        var second = new RetrievedChunk(
            new DocumentChunk("another-private-id", "another-private-doc", "another private body", "", 1, 2),
            new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/b.html"), "API Beta", 0.73, 2);
        var logger = new CapturingLogger();
        var reranker = new FakeReranker((_, candidates, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(
            [WithRerankMetadata(candidates[1], 0.95, 1), WithRerankMetadata(candidates[0], 0.75, 2)]));
        var generator = new FakeGenerator((_, _, _, _) => Task.FromResult(Answered()));
        var service = Service(search: Search([first, second]), reranker: reranker, generator: generator, logger: logger);

        await service.AnswerAsync(new UserQuestion(questionText), CancellationToken.None);

        var logText = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("API Alpha", logText, StringComparison.Ordinal);
        Assert.Contains("API Beta", logText, StringComparison.Ordinal);
        Assert.Contains("Movement API", logText, StringComparison.Ordinal);
        Assert.Contains("(none)", logText, StringComparison.Ordinal);
        Assert.Contains("SearchRank", logger.Properties.SelectMany(properties => properties.Keys));
        Assert.Contains("FinalRank", logger.Properties.SelectMany(properties => properties.Keys));
        Assert.Contains("EvidencePosition", logger.Properties.SelectMany(properties => properties.Keys));
        Assert.Contains("InitialRank", logger.Properties.SelectMany(properties => properties.Keys));
        Assert.Contains("SimilarityScore", logger.Properties.SelectMany(properties => properties.Keys));
        Assert.Contains("RerankerScore", logger.Properties.SelectMany(properties => properties.Keys));
        Assert.Contains(logger.Properties.SelectMany(properties => properties.Values), value => Equals(value, 0.82));
        Assert.Contains(logger.Properties.SelectMany(properties => properties.Values), value => Equals(value, 0.73));
        Assert.Contains(logger.Properties.SelectMany(properties => properties.Values), value => Equals(value, 0.95));
        Assert.Contains(logger.Properties.SelectMany(properties => properties.Values), value => Equals(value, 0.75));
        Assert.DoesNotContain(questionText, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(privateChunkText, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR_MARKER", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("CONNECTION_SECRET", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSWORD_SECRET", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-chunk-id", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-document-id", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullSearchAndNullRerankerResultsAreRejected()
    {
        var nullSearch = Service(search: new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(null!)));
        await Assert.ThrowsAsync<InvalidDataException>(() => nullSearch.AnswerAsync(new UserQuestion("q"), CancellationToken.None));

        var nullReranker = Service(reranker: new FakeReranker((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(null!)));
        await Assert.ThrowsAsync<InvalidDataException>(() => nullReranker.AnswerAsync(new UserQuestion("q"), CancellationToken.None));
    }

    [Fact]
    public async Task RerankerErrorsAreNotHiddenAndGeneratorIsNotCalled()
    {
        var expected = new InvalidOperationException("reranker");
        var generator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException("Should not generate."));
        var service = Service(reranker: new FakeReranker((_, _, _) => Task.FromException<IReadOnlyList<RetrievedChunk>>(expected)),
            generator: generator);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnswerAsync(new UserQuestion("question"), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task DetectorSearchRerankerAndGeneratorErrorsAreNotHidden()
    {
        var detectorError = new InvalidOperationException("detector");
        var detectorService = Service(detector: new FakeDetector((_, _) => Task.FromException<SupportedLanguage?>(detectorError)));
        Assert.Same(detectorError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            detectorService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));

        var searchError = new InvalidOperationException("search");
        var searchService = Service(search: new FakeSearch((_, _, _) => Task.FromException<IReadOnlyList<RetrievedChunk>>(searchError)));
        Assert.Same(searchError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            searchService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));

        var rerankerError = new InvalidOperationException("reranker");
        var rerankerService = Service(reranker: new FakeReranker((_, _, _) => Task.FromException<IReadOnlyList<RetrievedChunk>>(rerankerError)));
        Assert.Same(rerankerError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rerankerService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));

        var generationError = new InvalidOperationException("generation");
        var generationService = Service(generator: new FakeGenerator((_, _, _, _) => Task.FromException<RagAnswer>(generationError)));
        Assert.Same(generationError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generationService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));
    }

    [Fact]
    public async Task CancellationTokenReachesEveryStageIncludingReranker()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken detectorToken = default, searchToken = default, rerankerToken = default, generatorToken = default;
        var detector = new FakeDetector((_, token) => { detectorToken = token; return Task.FromResult<SupportedLanguage?>(SupportedLanguage.English); });
        var search = new FakeSearch((_, _, token) => { searchToken = token; return Task.FromResult<IReadOnlyList<RetrievedChunk>>([Chunk("one", 0.8)]); });
        var reranker = new FakeReranker((_, candidates, token) => { rerankerToken = token; return Task.FromResult(candidates); });
        var generator = new FakeGenerator((_, _, _, token) =>
        {
            generatorToken = token;
            return Task.FromResult(Answered());
        });

        await Service(detector, search, reranker, generator).AnswerAsync(new UserQuestion("question"), cancellation.Token);

        Assert.Equal(cancellation.Token, detectorToken);
        Assert.Equal(cancellation.Token, searchToken);
        Assert.Equal(cancellation.Token, rerankerToken);
        Assert.Equal(cancellation.Token, generatorToken);
    }

    [Theory]
    [InlineData(0, 0.25, 0.45, 3)]
    [InlineData(5, 0.25, 0.45, 0)]
    [InlineData(5, double.NaN, 0.45, 3)]
    [InlineData(5, 0.25, double.PositiveInfinity, 3)]
    [InlineData(5, 0.5, 0.45, 3)]
    [InlineData(5, -0.1, 0.45, 3)]
    [InlineData(5, 0.25, 1.1, 3)]
    [InlineData(5, 0.25, 0.45, 6)]
    public void InvalidConfigurationIsRejected(int topK, double domain, double evidence, int maxEvidence)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RagQueryService(
            EnglishDetector(), Search(Array.Empty<RetrievedChunk>()), IdentityReranker(), AnswerGenerator(), new CapturingLogger(),
            Profile, topK, domain, evidence, maxEvidence));
    }

    [Fact]
    public void AskOptionsRequireRerankerCapacityAtLeastTopK()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AskRunOptions
        {
            TopK = 5,
            RerankerMaxCandidates = 4
        }.Validate());

        var options = new AskRunOptions { TopK = 5, RerankerMaxCandidates = 5 };
        options.Validate();
        var reranker = options.CreateOllamaRerankerOptions();
        Assert.Equal(options.OllamaEndpoint, reranker.Endpoint);
        Assert.Equal("qwen3:4b", reranker.Model);
        Assert.False(reranker.Think);
        Assert.Equal("qwen3:4b", options.CreateOllamaGenerationOptions().Model);
    }

    private static RagQueryService Service(
        FakeDetector? detector = null,
        FakeSearch? search = null,
        FakeReranker? reranker = null,
        FakeGenerator? generator = null,
        int maxEvidence = 3,
        CapturingLogger? logger = null) => new(
        detector ?? EnglishDetector(), search ?? Search([Chunk("candidate", 0.7)]), reranker ?? IdentityReranker(),
        generator ?? AnswerGenerator(), logger ?? new CapturingLogger(), Profile, 5, 0.25, 0.45, maxEvidence);

    private static FakeDetector EnglishDetector() => new((_, _) => Task.FromResult<SupportedLanguage?>(SupportedLanguage.English));
    private static FakeSearch Search(IReadOnlyList<RetrievedChunk> candidates) => new((_, _, _) => Task.FromResult(candidates));
    private static FakeReranker IdentityReranker() => new((_, candidates, _) => Task.FromResult(candidates));
    private static FakeGenerator AnswerGenerator() => new((_, language, _, _) => Task.FromResult(Answered("generated", language)));
    private static RagAnswer Answered(string text = "generated", SupportedLanguage language = SupportedLanguage.English) => new(
        text, language, AnswerStatus.Answered, [new Citation("title", new Uri("https://example.test"))]);

    private static RetrievedChunk Chunk(string id, double score, double? rerankerScore = null, int? finalRank = null) => new(
        new DocumentChunk(id, "doc-" + id, "body", "Methods", 0, 1),
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/test.html"), id, score, 1,
        rerankerScore, finalRank);

    private static RetrievedChunk WithRerankMetadata(RetrievedChunk source, double rerankerScore, int finalRank) => new(
        source.Chunk, source.SourceUrl, source.SourceTitle, source.SimilarityScore, source.InitialRank, rerankerScore, finalRank);

    private sealed class FakeDetector(Func<UserQuestion, CancellationToken, Task<SupportedLanguage?>> detect) : ILanguageDetector
    {
        public Task<SupportedLanguage?> DetectAsync(UserQuestion question, CancellationToken cancellationToken) => detect(question, cancellationToken);
    }

    private sealed class FakeSearch(Func<RetrievalQuery, EmbeddingProfile, CancellationToken, Task<IReadOnlyList<RetrievedChunk>>> search)
        : ISemanticSearchService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, EmbeddingProfile profile, CancellationToken cancellationToken)
        {
            Calls++;
            return search(query, profile, cancellationToken);
        }
    }

    private sealed class FakeReranker(Func<UserQuestion, IReadOnlyList<RetrievedChunk>, CancellationToken, Task<IReadOnlyList<RetrievedChunk>>> rerank)
        : IReranker
    {
        public int Calls { get; private set; }
        public IReadOnlyList<RetrievedChunk>? ReceivedCandidates { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(UserQuestion question, IReadOnlyList<RetrievedChunk> candidates,
            CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedCandidates = candidates.ToArray();
            return rerank(question, candidates, cancellationToken);
        }
    }

    private sealed class FakeGenerator(Func<UserQuestion, SupportedLanguage, IReadOnlyList<RetrievedChunk>, CancellationToken, Task<RagAnswer>> generate)
        : IAnswerGenerator
    {
        public int Calls { get; private set; }
        public Task<RagAnswer> GenerateAsync(UserQuestion question, SupportedLanguage language, IReadOnlyList<RetrievedChunk> evidence,
            CancellationToken cancellationToken)
        {
            Calls++;
            return generate(question, language, evidence, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<RagQueryService>
    {
        public List<string> Messages { get; } = [];
        public List<IReadOnlyDictionary<string, object?>> Properties { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Properties.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }
}
