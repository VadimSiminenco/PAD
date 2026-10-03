using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Ingestion;

namespace UnityDocsRag.Tests.Ingestion;

public sealed class AskRunnerTests
{
    [Fact]
    public async Task CreatesQuestionAndReturnsServiceAnswer()
    {
        const string questionText = "How do I set a NavMeshAgent destination?";
        UserQuestion? received = null;
        using var cancellation = new CancellationTokenSource();
        CancellationToken receivedToken = default;
        var expected = Answer();
        var service = new FakeRagQueryService((question, token) =>
        {
            received = question;
            receivedToken = token;
            return Task.FromResult(expected);
        });
        var runner = new AskRunner(service, new CapturingLogger());

        var actual = await runner.RunAsync(questionText, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(questionText, received?.Text);
        Assert.Equal(cancellation.Token, receivedToken);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task LogsAnswerAndCitationMetadataButNotQuestionChunkVectorOrConnectionString()
    {
        const string question = "PRIVATE QUESTION";
        const string chunkText = "PRIVATE CHUNK vector 0.12345678";
        const string connectionString = "Host=private-host;Password=private-password";
        var answer = new RagAnswer("Grounded safe answer", SupportedLanguage.English, AnswerStatus.Answered,
            [new Citation("NavMeshAgent API", new Uri("https://docs.unity3d.com/6000.3/test.html"), "Methods")]);
        var logger = new CapturingLogger();
        var runner = new AskRunner(new FakeRagQueryService((_, _) => Task.FromResult(answer)), logger);

        await runner.RunAsync(question, CancellationToken.None);

        var messages = string.Join(" ", logger.Messages);
        Assert.Contains(nameof(AnswerStatus.Answered), messages, StringComparison.Ordinal);
        Assert.Contains(nameof(SupportedLanguage.English), messages, StringComparison.Ordinal);
        Assert.Contains("Grounded safe answer", messages, StringComparison.Ordinal);
        Assert.Contains("NavMeshAgent API", messages, StringComparison.Ordinal);
        Assert.Contains("Methods", messages, StringComparison.Ordinal);
        Assert.Contains("docs.unity3d.com", messages, StringComparison.Ordinal);
        Assert.DoesNotContain(question, messages, StringComparison.Ordinal);
        Assert.DoesNotContain(chunkText, messages, StringComparison.Ordinal);
        Assert.DoesNotContain("0.12345678", messages, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, messages, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n")]
    public async Task EmptyQuestionIsRejectedBeforeServiceCall(string? question)
    {
        var service = new FakeRagQueryService((_, _) => throw new InvalidOperationException("must not be called"));
        var runner = new AskRunner(service, new CapturingLogger());

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(question!, CancellationToken.None));

        Assert.Equal(0, service.Calls);
    }

    private static RagAnswer Answer() => new("Answer text", SupportedLanguage.Russian, AnswerStatus.Answered,
        [new Citation("title", new Uri("https://docs.unity3d.com/6000.3/test.html"), "section")]);

    private sealed class FakeRagQueryService(Func<UserQuestion, CancellationToken, Task<RagAnswer>> answer) : IRagQueryService
    {
        public int Calls { get; private set; }
        public Task<RagAnswer> AnswerAsync(UserQuestion question, CancellationToken cancellationToken)
        {
            Calls++;
            return answer(question, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<AskRunner>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
