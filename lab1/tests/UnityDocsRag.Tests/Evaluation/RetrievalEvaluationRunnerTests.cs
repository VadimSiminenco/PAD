using System.Text.Json;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Evaluation;

namespace UnityDocsRag.Tests.Evaluation;

public sealed class RetrievalEvaluationRunnerTests
{
    private static readonly EvaluationDataset Dataset = EvaluationDataset.Parse("""
        {
          "Version": 1,
          "UnityVersion": "6000.3",
          "Questions": [
            {
              "Id": "Q1", "Language": "ru", "Category": "InCorpus",
              "Text": "Первый вопрос?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": true,
              "ExpectedSourceUrls": ["https://example.test/a", "https://example.test/b"],
              "ExpectedFacts": ["Факт"]
            },
            {
              "Id": "Q2", "Language": "ru", "Category": "UnityWithoutCorpusEvidence",
              "Text": "Недоступный метод?", "ExpectedStatus": "InsufficientEvidence",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            },
            {
              "Id": "Q3", "Language": "en", "Category": "InCorpus",
              "Text": "Second question?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": ["https://example.test/c"],
              "ExpectedFacts": ["Another fact"]
            },
            {
              "Id": "Q4", "Language": "en", "Category": "OutOfDomain",
              "Text": "What is the weather?", "ExpectedStatus": "OutOfDomain",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            }
          ]
        }
        """);

    private static readonly EmbeddingProfile Profile = new("Ollama", "embeddinggemma", 768, true);

    [Fact]
    public async Task SearchesEachAnsweredQuestionOnceAndPreservesChunkOrderAndDuplicates()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var fake = new FakeSearch((query, profile, token) =>
        {
            Assert.Same(Profile, profile);
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(20, query.TopK);
            Assert.Equal(0.0, query.SimilarityThreshold);
            calls.Add(query.Text);
            IReadOnlyList<RetrievedChunk> results = query.Text == "Первый вопрос?"
                ? [Chunk("other", 1, 0.9), Chunk("other", 2, 0.8), Chunk("b", 3, 0.7)]
                : [Chunk("x", 1, 0.9), Chunk("y", 2, 0.8), Chunk("z", 3, 0.7),
                    Chunk("w", 4, 0.6), Chunk("c", 5, 0.5)];
            return Task.FromResult(results);
        });

        var report = await new RetrievalEvaluationRunner(fake).RunAsync(Dataset, Profile, cancellation.Token);

        Assert.Equal(["Первый вопрос?", "Second question?"], calls);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(1, report.DatasetVersion);
        Assert.Equal("6000.3", report.UnityVersion);
        Assert.Same(Profile, report.EmbeddingProfile);
        Assert.Equal(["Q1", "Q3"], report.Questions.Select(question => question.Id));
        Assert.Equal([1, 2, 3], report.Questions[0].RetrievedSources.Select(source => source.Rank));
        Assert.Equal([
            "https://example.test/other", "https://example.test/other", "https://example.test/b"
        ], report.Questions[0].RetrievedSources.Select(source => source.Url));
        Assert.Equal([0.9, 0.8, 0.7], report.Questions[0].RetrievedSources.Select(source => source.Similarity));
        Assert.Equal(1.0, report.Questions[0].HitAt3);
        Assert.Equal(1.0 / 3.0, report.Questions[0].ReciprocalRank);
        Assert.Equal(0.5, report.Questions[0].SourceCoverageAt3);
        Assert.Equal(0.5, report.Questions[0].SourceCoverageAt20);
        Assert.Equal(0.0, report.Questions[0].AllSourcesHitAt3);
        Assert.Equal(0.0, report.Questions[0].AllSourcesHitAt20);
        Assert.Equal(0.0, report.Questions[1].HitAt3);
        Assert.Equal(1.0, report.Questions[1].HitAt5);
        Assert.Equal(1.0 / 5.0, report.Questions[1].ReciprocalRank);
        Assert.Equal(0.0, report.Questions[1].SourceCoverageAt3);
        Assert.Equal(1.0, report.Questions[1].SourceCoverageAt5);
        Assert.Equal(0.0, report.Questions[1].AllSourcesHitAt3);
        Assert.Equal(1.0, report.Questions[1].AllSourcesHitAt5);
        Assert.Equal(2, report.Summary.AnsweredQuestionCount);
        Assert.Equal(0.5, report.Summary.HitRateAt3);
        Assert.Equal(1.0, report.Summary.HitRateAt5);
        Assert.Equal(1.0, report.Summary.HitRateAt10);
        Assert.Equal(1.0, report.Summary.HitRateAt20);
        Assert.Equal((1.0 / 3.0 + 1.0 / 5.0) / 2, report.Summary.MrrAt20);
        Assert.Equal(0.25, report.Summary.MeanSourceCoverageAt3);
        Assert.Equal(0.75, report.Summary.MeanSourceCoverageAt5);
        Assert.Equal(0.75, report.Summary.MeanSourceCoverageAt10);
        Assert.Equal(0.75, report.Summary.MeanSourceCoverageAt20);
        Assert.Equal(0.0, report.Summary.MeanAllSourcesHitAt3);
        Assert.Equal(0.5, report.Summary.MeanAllSourcesHitAt5);
        Assert.Equal(0.5, report.Summary.MeanAllSourcesHitAt10);
        Assert.Equal(0.5, report.Summary.MeanAllSourcesHitAt20);

        var json = JsonSerializer.Serialize(report);
        Assert.Contains("\"SourceCoverageAt3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"MeanAllSourcesHitAt20\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chunk secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Первый вопрос?", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connection string", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresResultsBeyondTopTwentyForMrrAtTwenty()
    {
        var results = Enumerable.Range(1, 21)
            .Select(rank => Chunk(rank == 21 ? "a" : "other", rank, 1.0 - rank / 100.0))
            .ToArray();
        var fake = new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(results));

        var report = await new RetrievalEvaluationRunner(fake).RunAsync(Dataset, Profile, CancellationToken.None);

        Assert.Equal(20, report.Questions[0].RetrievedSources.Count);
        Assert.Equal(0.0, report.Questions[0].HitAt20);
        Assert.Equal(0.0, report.Questions[0].ReciprocalRank);
        Assert.Equal(0.0, report.Questions[0].SourceCoverageAt20);
        Assert.Equal(0.0, report.Questions[0].AllSourcesHitAt20);
    }

    [Fact]
    public async Task NullSearchResultIsRejected()
    {
        var fake = new FakeSearch((_, _, _) => Task.FromResult<IReadOnlyList<RetrievedChunk>>(null!));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RetrievalEvaluationRunner(fake).RunAsync(Dataset, Profile, CancellationToken.None));
        Assert.Equal(1, fake.Calls);
    }

    private static RetrievedChunk Chunk(string urlSuffix, int rank, double similarity) => new(
        new DocumentChunk($"chunk-{rank}", "doc", "chunk secret", null, rank - 1, 2),
        new Uri($"https://example.test/{urlSuffix}"), "Title", similarity, rank);

    private sealed class FakeSearch(Func<RetrievalQuery, EmbeddingProfile, CancellationToken,
        Task<IReadOnlyList<RetrievedChunk>>> search) : ISemanticSearchService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
            RetrievalQuery query, EmbeddingProfile profile, CancellationToken cancellationToken)
        {
            Calls++;
            return search(query, profile, cancellationToken);
        }
    }
}
