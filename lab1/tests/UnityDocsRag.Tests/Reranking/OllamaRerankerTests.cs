using System.Net;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Reranking;

namespace UnityDocsRag.Tests.Reranking;

public sealed class OllamaRerankerTests
{
    [Fact]
    public async Task SendsConfiguredStructuredChatRequestAndReturnsPermutationWithStableMetadata()
    {
        var candidates = new[]
        {
            Candidate("A", "first body", "A.html", 0.91, 2),
            Candidate("B", "second body", "B.html", 0.72, 3),
            Candidate("C", "third body", "C.html", 0.33, 1)
        };
        var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Equal("3m", root.GetProperty("keep_alive").GetString());
            Assert.False(root.GetProperty("think").GetBoolean());
            Assert.Equal(new[] { "system", "user" }, root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("role").GetString()));
            var options = root.GetProperty("options");
            Assert.Equal(0, options.GetProperty("temperature").GetDouble());
            Assert.Equal(128, options.GetProperty("num_predict").GetInt32());
            Assert.Equal(2048, options.GetProperty("num_ctx").GetInt32());
            var schema = root.GetProperty("format");
            Assert.Equal(new[] { "rankedSourceNumbers" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(new[] { "rankedSourceNumbers" }, schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            return OllamaResponse(Structured(3, 1, 2));
        });
        using var client = new HttpClient(handler);
        var reranker = Reranker(client, new OllamaRerankerOptions
        {
            Model = "qwen3:4b", KeepAlive = "3m", Temperature = 0, NumPredict = 128, NumCtx = 2048
        });

        var result = await reranker.RerankAsync(new UserQuestion("question"), candidates, CancellationToken.None);

        Assert.Equal(new[] { "C", "A", "B" }, result.Select(candidate => candidate.SourceTitle));
        Assert.Equal(new int?[] { 1, 2, 3 }, result.Select(candidate => candidate.FinalRank).ToArray());
        Assert.Equal(new double?[] { 1.0, 0.5, 0.0 }, result.Select(candidate => candidate.RerankerScore).ToArray());
        for (var index = 0; index < result.Count; index++)
        {
            var original = candidates[new[] { 2, 0, 1 }[index]];
            Assert.Same(original.Chunk, result[index].Chunk);
            Assert.Equal(original.SourceUrl, result[index].SourceUrl);
            Assert.Equal(original.SourceTitle, result[index].SourceTitle);
            Assert.Equal(original.SimilarityScore, result[index].SimilarityScore);
            Assert.Equal(original.InitialRank, result[index].InitialRank);
        }
        Assert.Equal(new[] { "A", "B", "C" }, candidates.Select(candidate => candidate.SourceTitle));
        Assert.Null(candidates[0].FinalRank);
        Assert.Null(candidates[0].RerankerScore);
    }

    [Fact]
    public async Task ExplicitThinkFalseIsPassedToOllama()
    {
        var handler = new FakeHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.False(json.RootElement.GetProperty("think").GetBoolean());
            return OllamaResponse(Structured(1));
        });
        using var client = new HttpClient(handler);
        var reranker = Reranker(client, new OllamaRerankerOptions { Think = false });

        var result = await reranker.RerankAsync(new UserQuestion("question"),
            [Candidate("A", "body", "A.html", 0.5, 1)], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExplicitThinkTrueIsPassedToOllama()
    {
        var handler = new FakeHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.True(json.RootElement.GetProperty("think").GetBoolean());
            return OllamaResponse(Structured(1));
        });
        using var client = new HttpClient(handler);
        var reranker = Reranker(client, new OllamaRerankerOptions { Think = true });

        var result = await reranker.RerankAsync(new UserQuestion("question"),
            [Candidate("A", "body", "A.html", 0.5, 1)], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SingleCandidateGetsRankOneAndScoreOne()
    {
        var candidate = Candidate("Only", "body", "Only.html", 0.8, 4);
        using var client = new HttpClient(Responding(OllamaResponse(Structured(1))));

        var result = await Reranker(client).RerankAsync(new UserQuestion("question"), [candidate], CancellationToken.None);

        var actual = Assert.Single(result);
        Assert.Same(candidate.Chunk, actual.Chunk);
        Assert.Equal(1, actual.FinalRank);
        Assert.Equal(1.0, actual.RerankerScore);
    }

    [Fact]
    public async Task EmptyCandidatesReturnReadOnlyEmptyListWithoutHttp()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        using var client = new HttpClient(handler);

        var result = await Reranker(client).RerankAsync(new UserQuestion("question"), Array.Empty<RetrievedChunk>(), CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, handler.RequestCount);
        Assert.Throws<NotSupportedException>(() => ((IList<RetrievedChunk>)result).Add(Candidate("x", "x", "x.html", 0, 1)));
    }

    [Fact]
    public async Task TooManyCandidatesAreRejectedBeforeHttp()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        using var client = new HttpClient(handler);
        var reranker = Reranker(client, new OllamaRerankerOptions { MaxCandidates = 2 });

        await Assert.ThrowsAsync<ArgumentException>(() => reranker.RerankAsync(new UserQuestion("question"),
            [Candidate("A", "a", "A.html", 0.5, 1), Candidate("B", "b", "B.html", 0.4, 2), Candidate("C", "c", "C.html", 0.3, 3)], CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task NullCandidateIsRejectedBeforeHttp()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => Reranker(client).RerankAsync(new UserQuestion("question"),
            new RetrievedChunk[] { null! }, CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("{\"rankedSourceNumbers\":[1,1]}")]
    [InlineData("{\"rankedSourceNumbers\":[1]}")]
    [InlineData("{\"rankedSourceNumbers\":[1,3]}")]
    [InlineData("{\"rankedSourceNumbers\":[1,2,3]}")]
    public async Task IncompleteDuplicateOrOutOfRangePermutationIsRejected(string json)
    {
        await AssertInvalidResponseAsync(json, candidateCount: 2);
    }

    [Fact]
    public async Task UnsupportedExtraStructuredFieldIsRejected()
    {
        await AssertInvalidResponseAsync("{\"rankedSourceNumbers\":[1],\"explanation\":\"secret\"}", candidateCount: 1);
    }

    [Theory]
    [InlineData("outer")]
    [InlineData("inner")]
    [InlineData("missing-message")]
    [InlineData("missing-content")]
    public async Task MalformedOuterOrInnerResponseIsRejected(string kind)
    {
        var response = kind switch
        {
            "outer" => RawResponse("{"),
            "inner" => OllamaResponse("not json"),
            "missing-message" => RawResponse("{}"),
            _ => RawResponse("{\"message\":{}}")
        };
        using var client = new HttpClient(Responding(response));

        await Assert.ThrowsAsync<InvalidDataException>(() => Reranker(client).RerankAsync(new UserQuestion("question"),
            [Candidate("A", "body", "A.html", 0.5, 1)], CancellationToken.None));
    }

    [Fact]
    public async Task HttpFailureDoesNotRevealQuestionCandidatePromptOrBody()
    {
        const string question = "PRIVATE QUESTION marker";
        const string chunk = "PRIVATE CHUNK marker";
        const string body = "PRIVATE BODY marker";
        using var client = new HttpClient(Responding(RawResponse(body, HttpStatusCode.BadGateway)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Reranker(client).RerankAsync(
            new UserQuestion(question), [Candidate("Title", chunk, "A.html", 0.5, 1)], CancellationToken.None));

        Assert.DoesNotContain(question, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(chunk, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE BODY marker", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Rank the supplied", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationTokenIsPassedToHttpRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeHandler((_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reranker(client).RerankAsync(
            new UserQuestion("question"), [Candidate("A", "body", "A.html", 0.5, 1)], cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void OptionsRequireLoopbackAndValidNumericRanges()
    {
        Assert.False(new OllamaRerankerOptions().Think);
        Assert.Equal(512, new OllamaRerankerOptions().NumPredict);
        Assert.Throws<ArgumentException>(() => new OllamaRerankerOptions { Endpoint = "https://example.com" }.Validate());
        Assert.Throws<ArgumentException>(() => new OllamaRerankerOptions { Model = " " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaRerankerOptions { MaxCandidates = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaRerankerOptions { HttpTimeoutSeconds = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaRerankerOptions { Temperature = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaRerankerOptions { NumPredict = 5000 }.Validate());
    }

    private static async Task AssertInvalidResponseAsync(string structuredJson, int candidateCount)
    {
        using var client = new HttpClient(Responding(OllamaResponse(structuredJson)));
        var candidates = Enumerable.Range(1, candidateCount)
            .Select(index => Candidate("Title " + index, "body " + index, index + ".html", 0.5, index)).ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() => Reranker(client).RerankAsync(
            new UserQuestion("question"), candidates, CancellationToken.None));
    }

    private static OllamaReranker Reranker(HttpClient client, OllamaRerankerOptions? options = null) =>
        new(client, options ?? new OllamaRerankerOptions());

    private static RetrievedChunk Candidate(string title, string text, string file, double similarity, int initialRank) => new(
        new DocumentChunk("chunk-" + file, "doc-" + file, text, "Methods", 0, 3),
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/" + file), title, similarity, initialRank);

    private static string Structured(params int[] rankedSourceNumbers) =>
        JsonSerializer.Serialize(new { rankedSourceNumbers });

    private static HttpResponseMessage OllamaResponse(string content) =>
        RawResponse(JsonSerializer.Serialize(new { message = new { content } }));

    private static HttpResponseMessage RawResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static FakeHandler Responding(HttpResponseMessage response) => new((_, _) => Task.FromResult(response));

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
    }
}
