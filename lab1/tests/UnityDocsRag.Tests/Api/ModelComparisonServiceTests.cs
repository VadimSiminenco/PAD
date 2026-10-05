using UnityDocsRag.Api;
using System.Text.Json;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Tests.Api;

public sealed class ModelComparisonServiceTests
{
    private static readonly EmbeddingProfile Profile = new("Ollama", "embeddinggemma", 3, true);

    [Fact]
    public async Task SearchesAndReranksOnceThenRunsBothGeneratorsSequentiallyWithSameEvidence()
    {
        var order = new List<string>();
        IReadOnlyList<RetrievedChunk>? sharedEvidence = null;
        UserQuestion? firstQuestion = null;
        UserQuestion? secondQuestion = null;
        SupportedLanguage? firstLanguage = null;
        SupportedLanguage? secondLanguage = null;
        var search = new FakeSearch((_, _, _) => { order.Add("search"); return Task.FromResult<IReadOnlyList<RetrievedChunk>>([Chunk("candidate", .8)]); });
        var reranker = new FakeReranker((_, candidates, _) => { order.Add("rerank"); return Task.FromResult(candidates); });
        var runs = new[]
        {
            new ModelComparisonRun("model-a", new FakeGenerator((question, language, evidence, _) =>
            { order.Add("a-start"); firstQuestion = question; firstLanguage = language; sharedEvidence = evidence; order.Add("a-end"); return Task.FromResult(Answered("A")); })),
            new ModelComparisonRun("model-b", new FakeGenerator((question, language, evidence, _) =>
            { order.Add("b-start"); secondQuestion = question; secondLanguage = language; Assert.Same(sharedEvidence, evidence); order.Add("b-end"); return Task.FromResult(Answered("B")); }))
        };
        var result = await Service(new FakeDetector(SupportedLanguage.English), search, reranker, runs)
            .CompareAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(new[] { "search", "rerank", "a-start", "a-end", "b-start", "b-end" }, order);
        Assert.Equal(1, search.Calls);
        Assert.Equal(1, reranker.Calls);
        Assert.Same(firstQuestion, secondQuestion);
        Assert.Equal(SupportedLanguage.English, firstLanguage);
        Assert.Equal(firstLanguage, secondLanguage);
        Assert.True(result.IsComparison);
        Assert.All(result.Models, model => Assert.True(model.GenerationSeconds is >= 0));
        Assert.Equal(new[] { "A", "B" }, result.Models.Select(model => model.Text));
    }

    [Fact]
    public async Task RefusalDoesNotCallGeneratorsOrClaimModelComparison()
    {
        var search = new FakeSearch((_, _, _) => throw new InvalidOperationException("Must not search"));
        var runs = new[]
        {
            new ModelComparisonRun("a", new FakeGenerator((_, _, _, _) => throw new InvalidOperationException("Must not generate"))),
            new ModelComparisonRun("b", new FakeGenerator((_, _, _, _) => throw new InvalidOperationException("Must not generate")))
        };
        var result = await Service(new FakeDetector(null), search, new FakeReranker((_, _, _) => throw new InvalidOperationException()), runs)
            .CompareAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal("UnsupportedLanguage", result.Status);
        Assert.False(result.IsComparison);
        Assert.Empty(result.Models);
        Assert.Equal(0, search.Calls);
    }

    [Theory]
    [InlineData(.1, "OutOfDomain")]
    [InlineData(.3, "InsufficientEvidence")]
    public async Task SimilarityGatesReturnOneSharedStatusWithoutGeneration(double similarity, string expectedStatus)
    {
        var firstGenerator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException("Must not generate"));
        var secondGenerator = new FakeGenerator((_, _, _, _) => throw new InvalidOperationException("Must not generate"));
        var search = new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>([Chunk("candidate", similarity)]));
        var reranker = new FakeReranker((_, _, _) => throw new InvalidOperationException("Must not rerank"));
        var result = await Service(new FakeDetector(SupportedLanguage.English), search, reranker,
            [new ModelComparisonRun("a", firstGenerator), new ModelComparisonRun("b", secondGenerator)])
            .CompareAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.False(result.IsComparison);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task GeneratorFailureIsSafeAndSecondModelStillRuns()
    {
        const string secret = "raw prompt answer and internal exception";
        var runs = new[]
        {
            new ModelComparisonRun("model-a", new FakeGenerator((_, _, _, _) => throw new InvalidOperationException(secret))),
            new ModelComparisonRun("model-b", new FakeGenerator((_, _, _, _) => Task.FromResult(Answered("safe"))))
        };
        var result = await Service(new FakeDetector(SupportedLanguage.English), Search(), Rerank(), runs)
            .CompareAsync(new UserQuestion("question"), CancellationToken.None);

        Assert.Equal("Error", result.Models[0].Status);
        Assert.Null(result.Models[0].Text);
        Assert.DoesNotContain(secret, result.Models[0].SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Answered", result.Models[1].Status);
    }

    [Fact]
    public async Task SnapshotPersistsOnlyMeasurementMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "unitydocsrag-ab-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "last.json");
        try
        {
            var store = new FileModelComparisonSnapshotStore(path);
            await store.SaveAsync(new ModelComparisonSnapshot(DateTimeOffset.UtcNow, "Completed",
                [new ModelComparisonSnapshotModel("qwen3:4b", "Answered", 1.25)], 1.5), CancellationToken.None);
            var json = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(json);
            var propertyNames = EnumeratePropertyNames(document.RootElement).ToArray();

            Assert.Contains("qwen3:4b", json, StringComparison.Ordinal);
            Assert.DoesNotContain(propertyNames, name => name.Contains("question", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(propertyNames, name => name.Contains("answer", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(propertyNames, name => name.Contains("citation", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Completed", (await store.ReadAsync(CancellationToken.None))?.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelComparisonService Service(FakeDetector detector, FakeSearch search, FakeReranker reranker,
        IReadOnlyList<ModelComparisonRun> runs) => new(detector, search, reranker, Profile, 5, .25, .45, 3, runs);

    private static FakeSearch Search() => new((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>([Chunk("one", .8)]));
    private static FakeReranker Rerank() => new((_, chunks, _) => Task.FromResult(chunks));
    private static RagAnswer Answered(string text) => new(text, SupportedLanguage.English, AnswerStatus.Answered,
        [new Citation("API", new Uri("https://docs.unity3d.com/a"))]);
    private static RetrievedChunk Chunk(string id, double score) => new(
        new DocumentChunk(id, "doc-" + id, "body", "Description", 0, 1),
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/" + id + ".html"), id, score, 1);

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in EnumeratePropertyNames(item)) yield return nested;
        }
    }

    private sealed class FakeDetector(SupportedLanguage? language) : ILanguageDetector
    {
        public Task<SupportedLanguage?> DetectAsync(UserQuestion question, CancellationToken cancellationToken) => Task.FromResult(language);
    }
    private sealed class FakeSearch(Func<RetrievalQuery, EmbeddingProfile, CancellationToken, Task<IReadOnlyList<RetrievedChunk>>> run) : ISemanticSearchService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, EmbeddingProfile profile, CancellationToken cancellationToken)
        { Calls++; return run(query, profile, cancellationToken); }
    }
    private sealed class FakeReranker(Func<UserQuestion, IReadOnlyList<RetrievedChunk>, CancellationToken, Task<IReadOnlyList<RetrievedChunk>>> run) : IReranker
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(UserQuestion question, IReadOnlyList<RetrievedChunk> candidates, CancellationToken cancellationToken)
        { Calls++; return run(question, candidates, cancellationToken); }
    }
    private sealed class FakeGenerator(Func<UserQuestion, SupportedLanguage, IReadOnlyList<RetrievedChunk>, CancellationToken, Task<RagAnswer>> run) : IAnswerGenerator
    {
        public Task<RagAnswer> GenerateAsync(UserQuestion question, SupportedLanguage language, IReadOnlyList<RetrievedChunk> evidence, CancellationToken cancellationToken) => run(question, language, evidence, cancellationToken);
    }
}
