using System.Text.RegularExpressions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Generation;
using Xunit.Sdk;

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

    [Fact]
    public async Task AnswersRussianPathNavigationQuestionFromThreeSourcesWhenEndpointIsConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_OLLAMA_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        AssertLoopbackEndpoint(endpoint);

        var model = Environment.GetEnvironmentVariable("UNITYDOCS_TEST_GENERATION_MODEL");
        var options = new OllamaGenerationOptions
        {
            Endpoint = endpoint,
            Model = string.IsNullOrWhiteSpace(model) ? "qwen3:4b" : model,
            Temperature = 0,
            NumPredict = 256,
            NumCtx = 4096,
            HttpTimeoutSeconds = 1200,
            KeepAlive = "5m"
        };
        options.Validate();

        var setDestinationUrl = new Uri(
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.SetDestination.html");
        var warpUrl = new Uri(
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.Warp.html");
        var evidence = new[]
        {
            new RetrievedChunk(
                new DocumentChunk("live-path-description", "live-setdestination",
                    "Sets or updates the destination thus triggering the calculation for a new path. If a valid path becomes available then the agent will resume movement.",
                    "Description", 0, 25),
                setDestinationUrl, "NavMeshAgent.SetDestination", 0.91, 1),
            new RetrievedChunk(
                new DocumentChunk("live-path-parameters", "live-setdestination",
                    "public bool SetDestination(Vector3 target); target — The target point to navigate to.",
                    "Parameters", 1, 14),
                setDestinationUrl, "NavMeshAgent.SetDestination", 0.86, 2),
            new RetrievedChunk(
                new DocumentChunk("live-warp-description", "live-warp",
                    "Warps agent to the provided position.", "Description", 0, 7),
                warpUrl, "NavMeshAgent.Warp", 0.72, 3)
        };

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var generator = new OllamaAnswerGenerator(client, options);
        var answer = await generator.GenerateAsync(
            new UserQuestion("Как заставить NavMeshAgent добраться до целевой точки по рассчитанному пути, а не телепортироваться?"),
            SupportedLanguage.Russian, evidence, CancellationToken.None);

        try
        {
            Assert.Equal(AnswerStatus.Answered, answer.Status);
            Assert.Equal(SupportedLanguage.Russian, answer.Language);
            Assert.False(string.IsNullOrWhiteSpace(answer.Text));
            Assert.Matches("[\\u0400-\\u04FF]", answer.Text!);
            Assert.Contains("SetDestination", answer.Text!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(answer.Citations, citation =>
                citation.Title == "NavMeshAgent.SetDestination" && citation.Url == setDestinationUrl);
            Assert.False(RecommendsWarpForPathNavigation(answer.Text!),
                "The answer positively recommends Warp for the requested path navigation.");
        }
        catch (XunitException exception)
        {
            throw new XunitException($"{exception.Message}{Environment.NewLine}Full generated answer:{Environment.NewLine}{answer.Text}");
        }
    }

    [Theory]
    [InlineData("Используйте SetDestination, а не Warp, чтобы двигаться по рассчитанному пути.", false)]
    [InlineData("Не используйте Warp для движения по пути.", false)]
    [InlineData("Не следует использовать NavMeshAgent.Warp для движения по пути.", false)]
    [InlineData("Warp мгновенно телепортирует агента; для пути используйте SetDestination.", false)]
    [InlineData("Если нужна телепортация, используйте Warp. Для пути используйте SetDestination.", false)]
    [InlineData("Для телепортации используйте Warp; для движения по пути используйте SetDestination.", false)]
    [InlineData("NavMeshAgent.SetDestination используется для установки цели, что инициирует расчет нового пути. Если доступен действительный путь, то агент продолжит движение. Для мгновенного перемещения агента в заданную позицию следует использовать метод NavMeshAgent.Warp.", false)]
    [InlineData("Для навигации по рассчитанному пути используйте Warp.", true)]
    [InlineData("Для движения по рассчитанному пути используйте NavMeshAgent.Warp.", true)]
    [InlineData("Для мгновенного перемещения по рассчитанному пути используйте Warp.", true)]
    [InlineData("Чтобы добраться до цели по пути, вызовите NavMeshAgent.Warp.", true)]
    [InlineData("Можно использовать Warp для движения по рассчитанному пути.", true)]
    [InlineData("Для движения по рассчитанному пути применяйте Warp.", true)]
    [InlineData("Для пути подойдёт Warp.", true)]
    [InlineData("Не используйте SetDestination, используйте Warp для движения по пути.", true)]
    [InlineData("Используйте SetDestination или Warp для движения по пути.", true)]
    [InlineData("Warp можно использовать для навигации по пути.", true)]
    [InlineData("Warp подходит для движения по рассчитанному пути.", true)]
    public void WarpRecommendationCheckDistinguishesNegationAndPathAdvice(string answerText, bool expectedRecommendation)
    {
        Assert.Equal(expectedRecommendation, RecommendsWarpForPathNavigation(answerText));
    }

    private static bool RecommendsWarpForPathNavigation(string answerText)
    {
        var normalized = Regex.Replace(answerText, @"\bNavMeshAgent\.(?=[A-Za-z])", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var sentence in Regex.Split(normalized, @"[.!?;\r\n]+"))
        {
            var clauses = sentence.Split(',');
            for (var index = 0; index < clauses.Length; index++)
            {
                var clause = clauses[index];
                if (!Regex.IsMatch(clause, @"\bWarp\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;

                var recommends = Regex.IsMatch(clause,
                    @"\b(?:используйте|вызовите|примените|применяйте|выберите|использовать|вызвать|применить|применять|подойдёт|подойдет)\b.{0,80}\bWarp\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(clause,
                        @"\bWarp\b.{0,50}\b(?:можно|нужно|следует|рекомендуется)\s+(?:использовать|вызвать|применить|применять)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(clause,
                        @"\bWarp\b.{0,50}\b(?:подходит|позволяет)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!recommends) continue;

                var negated = Regex.IsMatch(clause,
                    @"\b(?:не|нельзя)\b.{0,30}\b(?:используйте|вызовите|примените|применяйте|использовать|вызвать|применить|применять|подойдёт|подойдет)\b.{0,50}\bWarp\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(clause, @"\bWarp\b.{0,25}\b(?:не|нельзя)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (negated) continue;

                var teleportationOnly = Regex.IsMatch(clause,
                    @"\b(?:для\s+(?:мгновенн\w+\s+)?телепортац\w*|для\s+мгновенн\w+\s+перемещен\w*|чтобы\s+телепортир\w*)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    index > 0 && Regex.IsMatch(clauses[index - 1],
                        @"\bесли\b.{0,60}\b(?:нужна|требуется)\s+телепортац\w*\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var pathNavigation = Regex.IsMatch(clause, @"\b(?:путь|пути|маршрут\w*|навигац\w*)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!teleportationOnly || pathNavigation) return true;
            }
        }

        return false;
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
