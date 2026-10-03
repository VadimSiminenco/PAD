using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Query;

namespace UnityDocsRag.Tests.Query;

public sealed class RagQueryServiceTests
{
    private static readonly EmbeddingProfile Profile = new("Ollama", "embeddinggemma", 3, true);

    [Fact]
    public async Task UnsupportedLanguageDoesNotSearchOrGenerate()
    {
        var search = new FakeSearch((_, _, _) => throw new InvalidOperationException());
        var generator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException());
        var service = Service(new FakeDetector((_, _) => Task.FromResult<SupportedLanguage?>(null)), search, generator);

        var answer = await service.AnswerAsync(new UserQuestion("unsupported"), CancellationToken.None);

        Assert.Equal(AnswerStatus.UnsupportedLanguage, answer.Status);
        Assert.Null(answer.Language);
        Assert.Contains("Russian and English", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, search.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task EmptyResultsReturnOutOfDomain()
    {
        var service = Service(search: new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(Array.Empty<RetrievedChunk>())));

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.OutOfDomain, answer.Status);
        Assert.Equal(SupportedLanguage.English, answer.Language);
    }

    [Fact]
    public async Task TopScoreBelowDomainThresholdReturnsOutOfDomain()
    {
        var service = Service(search: Search(0.249));

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.OutOfDomain, answer.Status);
    }

    [Fact]
    public async Task ScoreBetweenThresholdsReturnsInsufficientEvidence()
    {
        var service = Service(search: Search(0.3));

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.InsufficientEvidence, answer.Status);
    }

    [Fact]
    public async Task EvidenceThresholdIsInclusiveAndSearchHasNoSimilarityCutoff()
    {
        var search = new FakeSearch((query, _, _) =>
        {
            Assert.Equal(5, query.TopK);
            Assert.Null(query.SimilarityThreshold);
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(new[] { Chunk("eligible", 0.45) });
        });
        var generator = new FakeGenerator((_, _, evidence, _) =>
        {
            Assert.Single(evidence);
            return Task.FromResult(new RagAnswer("generated", SupportedLanguage.English, AnswerStatus.Answered,
                [new Citation("title", new Uri("https://example.test"))]));
        });
        var service = Service(search, generator);

        var answer = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task FiltersWeakCandidatesCapsEvidenceAndPreservesOrder()
    {
        var candidates = new[]
        {
            Chunk("first", 0.91), Chunk("weak", 0.44), Chunk("second", 0.8), Chunk("third", 0.7)
        };
        var generator = new FakeGenerator((_, _, evidence, _) =>
        {
            Assert.Equal(new[] { "first", "second", "third" }, evidence.Select(item => item.Chunk.ChunkId));
            return Task.FromResult(new RagAnswer("generated", SupportedLanguage.English, AnswerStatus.Answered,
                [new Citation("title", new Uri("https://example.test"))]));
        });
        var service = Service(new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(candidates)), generator);

        await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task ReturnsGeneratorAnswerWithoutChangingIt()
    {
        var expected = new RagAnswer("generated unchanged", SupportedLanguage.English, AnswerStatus.Answered,
            [new Citation("title", new Uri("https://example.test"))]);
        var generator = new FakeGenerator((_, _, _, _) => Task.FromResult(expected));
        var service = Service(Search(0.7), generator);

        var actual = await service.AnswerAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task DetectorSearchAndGeneratorErrorsAreNotHidden()
    {
        var detectorError = new InvalidOperationException("detector");
        var detectorService = Service(new FakeDetector((_, _) => Task.FromException<SupportedLanguage?>(detectorError)));
        Assert.Same(detectorError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            detectorService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));

        var searchError = new InvalidOperationException("search");
        var searchService = Service(search: new FakeSearch((_, _, _) => Task.FromException<IReadOnlyList<RetrievedChunk>>(searchError)));
        Assert.Same(searchError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            searchService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));

        var generationError = new InvalidOperationException("generation");
        var generationService = Service(Search(0.7), new FakeGenerator((_, _, _, _) => Task.FromException<RagAnswer>(generationError)));
        Assert.Same(generationError, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generationService.AnswerAsync(new UserQuestion("question"), CancellationToken.None)));
    }

    [Fact]
    public async Task CancellationTokenReachesDetectorSearchAndGenerator()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken detectorToken = default, searchToken = default, generatorToken = default;
        var detector = new FakeDetector((_, token) => { detectorToken = token; return Task.FromResult<SupportedLanguage?>(SupportedLanguage.English); });
        var search = new FakeSearch((_, _, token) => { searchToken = token; return Task.FromResult<IReadOnlyList<RetrievedChunk>>(new[] { Chunk("one", 0.8) }); });
        var generator = new FakeGenerator((_, _, _, token) =>
        {
            generatorToken = token;
            return Task.FromResult(new RagAnswer("answer", SupportedLanguage.English, AnswerStatus.Answered,
                [new Citation("title", new Uri("https://example.test"))]));
        });

        await Service(detector, search, generator).AnswerAsync(new UserQuestion("question"), cancellation.Token);

        Assert.Equal(cancellation.Token, detectorToken);
        Assert.Equal(cancellation.Token, searchToken);
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
            new FakeDetector((_, _) => Task.FromResult<SupportedLanguage?>(SupportedLanguage.English)),
            new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(Array.Empty<RetrievedChunk>())),
            new FakeGenerator((_, _, _, _) => throw new InvalidOperationException()),
            Profile, topK, domain, evidence, maxEvidence));
    }

    private static RagQueryService Service(
        FakeDetector? detector = null,
        FakeSearch? search = null,
        FakeGenerator? generator = null) => new(
            detector ?? EnglishDetector(), search ?? Search(0.7), generator ?? AnswerGenerator(), Profile, 5, 0.25, 0.45, 3);

    private static RagQueryService Service(FakeSearch search, FakeGenerator generator) => Service(EnglishDetector(), search, generator);

    private static FakeDetector EnglishDetector() => new((_, _) => Task.FromResult<SupportedLanguage?>(SupportedLanguage.English));
    private static FakeSearch Search(double score) => new((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(new[] { Chunk("candidate", score) }));
    private static FakeGenerator AnswerGenerator() => new((_, language, _, _) => Task.FromResult(new RagAnswer(
        "generated", language, AnswerStatus.Answered, [new Citation("title", new Uri("https://example.test"))])));

    private static RetrievedChunk Chunk(string id, double score) => new(
        new DocumentChunk(id, "doc-" + id, "body", "Methods", 0, 1),
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/test.html"), id, score, 1);

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
}
