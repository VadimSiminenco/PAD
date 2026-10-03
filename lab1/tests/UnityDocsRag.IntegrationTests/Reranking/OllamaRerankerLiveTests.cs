using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Reranking;
using Xunit.Sdk;

namespace UnityDocsRag.IntegrationTests.Reranking;

public sealed class OllamaRerankerLiveTests
{
    private static readonly (string Title, string Text, string File)[] CandidateData =
    [
        ("NavMeshAgent.Move", "Applies relative movement to the current position.", "AI.NavMeshAgent.Move.html"),
        ("NavMeshAgent.Warp", "Teleports or warps the agent to a position.", "AI.NavMeshAgent.Warp.html"),
        ("NavMeshAgent.SetDestination", "Sets or updates a destination and triggers path calculation.", "AI.NavMeshAgent.SetDestination.html"),
        ("NavMeshAgent.destination", "Gets or attempts to set the world-space destination.", "AI.NavMeshAgent-destination.html")
    ];

    [Theory]
    [InlineData("Russian navigation", "Как заставить NavMeshAgent добраться до целевой точки по рассчитанному пути, а не телепортироваться?", "NavMeshAgent.SetDestination")]
    [InlineData("English navigation", "How do I make a NavMeshAgent navigate to a target position along a calculated path instead of teleporting it?", "NavMeshAgent.SetDestination")]
    [InlineData("Russian instant movement", "Как мгновенно телепортировать NavMeshAgent в указанную позицию без движения по пути?", "NavMeshAgent.Warp")]
    [InlineData("English instant movement", "How do I instantly teleport a NavMeshAgent to a specified position without following a path?", "NavMeshAgent.Warp")]
    public async Task ReranksExplicitMovementIntentWhenLocalOllamaIsConfigured(
        string languageAndIntentCase,
        string questionText,
        string expectedFirstTitle)
    {
        var endpoint = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_OLLAMA_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        AssertLoopbackEndpoint(endpoint);

        var model = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_GENERATION_MODEL");
        var options = new OllamaRerankerOptions
        {
            Endpoint = endpoint,
            Model = string.IsNullOrWhiteSpace(model) ? "qwen3:4b" : model,
            KeepAlive = "5m",
            HttpTimeoutSeconds = 1200,
            Temperature = 0,
            NumPredict = 512,
            NumCtx = 4096,
            MaxCandidates = 5,
            Think = false
        };
        options.Validate();

        var candidates = CandidateData.Select((data, index) => new RetrievedChunk(
            new DocumentChunk("live-rerank-chunk-" + index, "live-rerank-doc-" + index, data.Text,
                "NavMeshAgent", index, 10),
            new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/" + data.File),
            data.Title,
            0.95 - index * 0.1,
            index + 1)).ToArray();

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reranker = new OllamaReranker(client, options);
        var receivedOrder = "<no result returned>";
        try
        {
            var result = await reranker.RerankAsync(new UserQuestion(questionText), candidates, CancellationToken.None);
            receivedOrder = string.Join(" > ", result.Select(item => item.SourceTitle));

            Assert.Equal(candidates.Length, result.Count);
            Assert.Equal(expectedFirstTitle, result[0].SourceTitle);
            Assert.Equal(Enumerable.Range(1, candidates.Length).Select(rank => (int?)rank).ToArray(),
                result.Select(item => item.FinalRank).ToArray());
            Assert.Equal(candidates.Select(item => item.Chunk.ChunkId).OrderBy(value => value, StringComparer.Ordinal),
                result.Select(item => item.Chunk.ChunkId).OrderBy(value => value, StringComparer.Ordinal));
            Assert.Equal(candidates.Select(item => item.SourceUrl).OrderBy(value => value.AbsoluteUri, StringComparer.Ordinal),
                result.Select(item => item.SourceUrl).OrderBy(value => value.AbsoluteUri, StringComparer.Ordinal));
            for (var index = 0; index < result.Count; index++)
            {
                var actual = result[index];
                var source = candidates.Single(item => item.Chunk.ChunkId == actual.Chunk.ChunkId);
                Assert.Same(source.Chunk, actual.Chunk);
                Assert.Equal(source.SourceUrl, actual.SourceUrl);
                Assert.Equal(source.SourceTitle, actual.SourceTitle);
                Assert.Equal(source.SimilarityScore, actual.SimilarityScore);
                Assert.Equal(source.InitialRank, actual.InitialRank);
                Assert.Equal(1.0 - (double)index / (result.Count - 1), actual.RerankerScore);
            }
        }
        catch (Exception exception)
        {
            throw new XunitException(
                $"Live {languageAndIntentCase} reranker check failed ({exception.GetType().Name}: {exception.Message}). Question: {questionText} Expected first title: {expectedFirstTitle}. Received title order: {receivedOrder}");
        }
    }

    private static void AssertLoopbackEndpoint(string endpointText)
    {
        Assert.True(Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint));
        Assert.NotNull(endpoint);
        Assert.Contains(endpoint!.Scheme, new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps });
        Assert.True(
            string.Equals(endpoint.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(endpoint.Host.Trim('[', ']'), "::1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(endpoint.AbsolutePath, new[] { "", "/" });
        Assert.Empty(endpoint.UserInfo);
        Assert.Empty(endpoint.Query);
        Assert.Empty(endpoint.Fragment);
    }
}
