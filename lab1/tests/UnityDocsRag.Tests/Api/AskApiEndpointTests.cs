using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnityDocsRag.Api;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Tests.Api;

public sealed class AskApiEndpointTests
{
    [Fact]
    public async Task ReturnsAnswerAndCitationsAndPassesQuestionAndCancellationToken()
    {
        UserQuestion? receivedQuestion = null;
        CancellationToken receivedToken = default;
        var expectedUrl = new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.Move.html");
        var service = new FakeRagQueryService((question, token) =>
        {
            receivedQuestion = question;
            receivedToken = token;
            return Task.FromResult(new RagAnswer("Ответ <script>не выполняется</script>", SupportedLanguage.Russian,
                AnswerStatus.Answered, [new Citation("NavMeshAgent.Move", expectedUrl, "Description")]));
        });
        using var cancellation = new CancellationTokenSource();

        var result = await AskApiEndpoint.HandleAsync(new AskRequest("Как действует Move?"), service,
            NullLogger<AskApiEndpoint>.Instance, cancellation.Token);
        var (statusCode, body) = await ExecuteAsync(result);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal("Как действует Move?", receivedQuestion?.Text);
        Assert.Equal(cancellation.Token, receivedToken);
        Assert.Equal("Answered", root.GetProperty("status").GetString());
        Assert.Equal("Russian", root.GetProperty("language").GetString());
        Assert.Equal("Ответ <script>не выполняется</script>", root.GetProperty("text").GetString());
        var citation = Assert.Single(root.GetProperty("citations").EnumerateArray());
        Assert.Equal("NavMeshAgent.Move", citation.GetProperty("title").GetString());
        Assert.Equal("Description", citation.GetProperty("section").GetString());
        Assert.Equal(expectedUrl.AbsoluteUri, citation.GetProperty("url").GetString());
        Assert.Equal(1, service.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsEmptyQuestionWithoutCallingService(string? question)
    {
        var service = new FakeRagQueryService((_, _) => throw new InvalidOperationException("Should not be called."));

        var result = await AskApiEndpoint.HandleAsync(new AskRequest(question), service,
            NullLogger<AskApiEndpoint>.Instance, CancellationToken.None);
        var (statusCode, body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains("Введите вопрос", body, StringComparison.Ordinal);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task RejectsQuestionOverLimitWithoutCallingService()
    {
        var service = new FakeRagQueryService((_, _) => throw new InvalidOperationException("Should not be called."));

        var result = await AskApiEndpoint.HandleAsync(new AskRequest(new string('x', AskApiEndpoint.MaxQuestionLength + 1)),
            service, NullLogger<AskApiEndpoint>.Instance, CancellationToken.None);
        var (statusCode, body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains(AskApiEndpoint.MaxQuestionLength.ToString(), body, StringComparison.Ordinal);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task HidesInternalExceptionDetailsFromResponseAndLog()
    {
        const string privateDetail = "PRIVATE question chunk prompt connection-string raw-Ollama-body";
        var logger = new CapturingLogger<AskApiEndpoint>();
        var service = new FakeRagQueryService((_, _) => Task.FromException<RagAnswer>(new InvalidOperationException(privateDetail)));

        var result = await AskApiEndpoint.HandleAsync(new AskRequest("Question text"), service, logger, CancellationToken.None);
        var (statusCode, body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);
        Assert.Contains("Не удалось получить ответ", body, StringComparison.Ordinal);
        Assert.DoesNotContain(privateDetail, body, StringComparison.Ordinal);
        var log = Assert.Single(logger.Messages);
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.DoesNotContain(privateDetail, log, StringComparison.Ordinal);
    }

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(_ => { });
        using var serviceProvider = services.BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class FakeRagQueryService(Func<UserQuestion, CancellationToken, Task<RagAnswer>> answer) : IRagQueryService
    {
        public int CallCount { get; private set; }

        public Task<RagAnswer> AnswerAsync(UserQuestion question, CancellationToken cancellationToken)
        {
            CallCount++;
            return answer(question, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
