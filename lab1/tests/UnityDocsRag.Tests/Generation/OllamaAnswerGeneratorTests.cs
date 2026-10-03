using System.Net;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;
using UnityDocsRag.Infrastructure.Generation;

namespace UnityDocsRag.Tests.Generation;

public sealed class OllamaAnswerGeneratorTests
{
    [Fact]
    public async Task SendsStructuredNonStreamingChatRequestWithConfiguredModelAndOptions()
    {
        var context = new[] { Source("GameObject", "Description", "A Unity object.", "GameObject.html") };
        var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("think").GetBoolean());
            Assert.Equal("2m", root.GetProperty("keep_alive").GetString());
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(new[] { "system", "user" }, messages.Select(item => item.GetProperty("role").GetString()));
            Assert.Contains("SOURCE 1", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
            var settings = root.GetProperty("options");
            Assert.Equal(0, settings.GetProperty("temperature").GetDouble());
            Assert.Equal(256, settings.GetProperty("num_predict").GetInt32());
            Assert.Equal(2048, settings.GetProperty("num_ctx").GetInt32());
            var format = root.GetProperty("format");
            Assert.Equal("object", format.GetProperty("type").GetString());
            Assert.False(format.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(new[] { "sufficientEvidence", "answer", "citedSourceNumbers" },
                format.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(new[] { "sufficientEvidence", "answer", "citedSourceNumbers" },
                format.GetProperty("properties").EnumerateObject().Select(property => property.Name));
            var properties = format.GetProperty("properties");
            Assert.Contains("directly supports a useful, correct answer",
                properties.GetProperty("sufficientEvidence").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("only facts supported by the supplied sources",
                properties.GetProperty("answer").GetProperty("description").GetString(), StringComparison.Ordinal);
            var citationSchema = properties.GetProperty("citedSourceNumbers");
            Assert.Contains("numbers of the supplied SOURCE records actually used",
                citationSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.True(citationSchema.GetProperty("uniqueItems").GetBoolean());
            return OllamaResponse(Structured(true, "Grounded answer", 1));
        });
        using var client = new HttpClient(handler);
        var generator = Generator(client, new OllamaGenerationOptions
        {
            Model = "qwen3:4b", KeepAlive = "2m", Temperature = 0,
            NumPredict = 256, NumCtx = 2048
        });

        var answer = await generator.GenerateAsync(new UserQuestion("question"), SupportedLanguage.English, context, CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(AnswerStatus.Answered, answer.Status);
    }

    [Theory]
    [InlineData(SupportedLanguage.Russian, "Объект создаётся через Instantiate.")]
    [InlineData(SupportedLanguage.English, "The object is created with Instantiate.")]
    public async Task ReturnsLocalizedAnswerAndCitationsInModelSpecifiedOrder(SupportedLanguage language, string answerText)
    {
        var context = new[]
        {
            Source("First title", "First section", "first text", "First.html"),
            Source("Second title", "Second section", "second text", "Second.html")
        };
        using var client = new HttpClient(Responding(OllamaResponse(Structured(true, answerText, 2, 1))));
        var generator = Generator(client);

        var answer = await generator.GenerateAsync(new UserQuestion("question"), language, context, CancellationToken.None);

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(language, answer.Language);
        Assert.Equal(answerText, answer.Text);
        Assert.Equal(new[] { "Second title", "First title" }, answer.Citations.Select(citation => citation.Title));
        Assert.Equal(context[1].SourceUrl, answer.Citations[0].Url);
        Assert.Equal("Second section", answer.Citations[0].Section);
        Assert.Equal(context[0].SourceUrl, answer.Citations[1].Url);
        Assert.Equal("First section", answer.Citations[1].Section);
    }

    [Fact]
    public async Task EmptyContextReturnsInsufficientEvidenceWithoutHttp()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP request."));
        using var client = new HttpClient(handler);
        var generator = Generator(client);

        var answer = await generator.GenerateAsync(new UserQuestion("question"), SupportedLanguage.Russian,
            Array.Empty<RetrievedChunk>(), CancellationToken.None);

        Assert.Equal(AnswerStatus.InsufficientEvidence, answer.Status);
        Assert.Equal(SupportedLanguage.Russian, answer.Language);
        Assert.Contains("недостаточно", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(answer.Citations);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task InsufficientEvidenceResponseReturnsLocalizedTextWithoutCitations()
    {
        var context = new[] { Source("Title", "Section", "text", "Title.html") };
        using var client = new HttpClient(Responding(OllamaResponse(Structured(false, "ignored model text", 1))));
        var answer = await Generator(client).GenerateAsync(new UserQuestion("question"), SupportedLanguage.English,
            context, CancellationToken.None);

        Assert.Equal(AnswerStatus.InsufficientEvidence, answer.Status);
        Assert.Equal(SupportedLanguage.English, answer.Language);
        Assert.Contains("not contain enough information", answer.Text, StringComparison.Ordinal);
        Assert.Empty(answer.Citations);
    }

    [Fact]
    public async Task AnsweredResponseWithEmptyTextIsRejected()
    {
        await AssertInvalidStructuredAsync(Structured(true, " ", 1));
    }

    [Fact]
    public async Task AnsweredResponseWithoutCitationsIsRejected()
    {
        await AssertInvalidStructuredAsync(Structured(true, "An answer", Array.Empty<int>()));
    }

    [Fact]
    public async Task OutOfRangeSourceNumberIsRejected()
    {
        await AssertInvalidStructuredAsync(Structured(true, "An answer", 2));
    }

    [Fact]
    public async Task DuplicateSourceNumbersAreRejected()
    {
        await AssertInvalidStructuredAsync(Structured(true, "An answer", 1, 1));
    }

    [Fact]
    public async Task ModelGeneratedUrlInAnswerIsRejected()
    {
        await AssertInvalidStructuredAsync(Structured(true, "See https://example.invalid/page", 1));
    }

    [Theory]
    [InlineData("outer")]
    [InlineData("inner")]
    public async Task MalformedOuterOrInnerJsonIsRejected(string malformedLayer)
    {
        var content = malformedLayer == "outer" ? "{" : "not valid inner json";
        var body = malformedLayer == "outer" ? content : JsonSerializer.Serialize(new { message = new { content } });
        using var client = new HttpClient(Responding(RawResponse(body)));
        var generator = Generator(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(new UserQuestion("question"),
            SupportedLanguage.English, [Source("Title", "Section", "text", "Title.html")], CancellationToken.None));
    }

    [Fact]
    public async Task HttpFailureDoesNotRevealQuestionChunkPromptOrResponseBody()
    {
        const string question = "PRIVATE QUESTION marker";
        const string chunkText = "PRIVATE CHUNK marker";
        const string responseSecret = "PRIVATE RESPONSE marker";
        using var client = new HttpClient(Responding(RawResponse(responseSecret, HttpStatusCode.BadGateway)));
        var generator = Generator(client);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(new UserQuestion(question),
            SupportedLanguage.English, [Source("Title", "Section", chunkText, "Title.html")], CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.DoesNotContain(question, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(chunkText, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(responseSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("You answer questions", exception.ToString(), StringComparison.Ordinal);
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
        var generator = Generator(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(new UserQuestion("question"),
            SupportedLanguage.English, [Source("Title", "Section", "text", "Title.html")], cancellation.Token));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void GenerationOptionsValidateLoopbackAndSafeNumericRanges()
    {
        Assert.Throws<ArgumentException>(() => new OllamaGenerationOptions { Endpoint = "https://example.com" }.Validate());
        Assert.Throws<ArgumentException>(() => new OllamaGenerationOptions { Model = " " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaGenerationOptions { NumPredict = 3000 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaGenerationOptions { NumCtx = 128 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaGenerationOptions { Temperature = double.NaN }.Validate());
    }

    private static async Task AssertInvalidStructuredAsync(string structured)
    {
        using var client = new HttpClient(Responding(OllamaResponse(structured)));
        var generator = Generator(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(new UserQuestion("question"),
            SupportedLanguage.English, [Source("Title", "Section", "text", "Title.html")], CancellationToken.None));
    }

    private static OllamaAnswerGenerator Generator(HttpClient client, OllamaGenerationOptions? options = null) =>
        new(client, options ?? new OllamaGenerationOptions());

    private static RetrievedChunk Source(string title, string section, string text, string file) => new(
        new DocumentChunk("chunk-id-" + file, "doc-id-" + file, text, section, 0, 2),
        new Uri($"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/{file}"), title, 0.9, 1);

    private static string Structured(bool sufficientEvidence, string answer, params int[] sourceNumbers) =>
        JsonSerializer.Serialize(new { sufficientEvidence, answer, citedSourceNumbers = sourceNumbers });

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
