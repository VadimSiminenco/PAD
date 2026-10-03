using System.Text.RegularExpressions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Generation;

namespace UnityDocsRag.IntegrationTests.Generation;

public sealed class OllamaAnswerGeneratorLiveTests
{
    private const string ContextText =
        "NavMeshAgent.SetDestination(Vector3 target) sets or updates the destination and starts calculation of a new path. " +
        "Call agent.SetDestination(targetPosition), where targetPosition is the desired Vector3 destination. The method returns bool.";

    [Fact]
    public async Task GeneratesGroundedAnswersInEnglishAndRussianWhenEndpointIsConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_OLLAMA_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        AssertLoopbackEndpoint(endpoint);

        var model = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_GENERATION_MODEL");
        var options = new OllamaGenerationOptions
        {
            Endpoint = endpoint,
            Model = string.IsNullOrWhiteSpace(model) ? "qwen3:4b" : model,
            Temperature = 0,
            NumPredict = 128,
            NumCtx = 2048,
            HttpTimeoutSeconds = 1200,
            KeepAlive = "5m"
        };

        options.Validate();

        var chunk = new DocumentChunk(
            "live-test-navmeshagent-setdestination",
            "live-test-navmeshagent",
            ContextText,
            "NavMeshAgent.SetDestination",
            ordinal: 0,
            approximateTokenCount: 13);
        var source = new RetrievedChunk(
            chunk,
            new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.SetDestination.html"),
            "NavMeshAgent.SetDestination",
            similarityScore: 1,
            initialRank: 1);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var generator = new OllamaAnswerGenerator(client, options);

        await AssertAnswerAsync(
            generator,
            source,
            "How do I set a destination for a NavMeshAgent?",
            SupportedLanguage.English,
            new Regex("[A-Za-z]", RegexOptions.CultureInvariant));
        await AssertAnswerAsync(
            generator,
            source,
            "Как задать точку назначения для NavMeshAgent?",
            SupportedLanguage.Russian,
            new Regex("[\\u0400-\\u04FF]", RegexOptions.CultureInvariant));
    }

    private static async Task AssertAnswerAsync(
        OllamaAnswerGenerator generator,
        RetrievedChunk source,
        string questionText,
        SupportedLanguage language,
        Regex expectedScript)
    {
        var answer = await generator.GenerateAsync(
            new UserQuestion(questionText),
            language,
            new[] { source },
            CancellationToken.None);

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.Matches(expectedScript, answer.Text!);

        var citation = Assert.Single(answer.Citations);
        Assert.Equal(source.SourceUrl, citation.Url);
        Assert.Equal(source.SourceTitle, citation.Title);
        Assert.Equal(source.Chunk.Section, citation.Section);
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
