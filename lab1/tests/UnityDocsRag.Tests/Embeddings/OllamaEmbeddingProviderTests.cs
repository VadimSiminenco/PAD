using System.Net;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Embeddings;

namespace UnityDocsRag.Tests.Embeddings;

public sealed class OllamaEmbeddingProviderTests
{
    private static readonly EmbeddingProfile Profile = new("Ollama", "embeddinggemma", 3, multilingual: true);

    [Fact]
    public async Task CorrectBatchRequestAndResponsePreserveOrder()
    {
        var chunks = new[] { Chunk("a", "first text"), Chunk("b", "second text") };
        var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:11434/api/embed", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            var dto = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token)).RootElement;
            Assert.Equal("embeddinggemma", dto.GetProperty("model").GetString());
            Assert.Equal(new[] { "first text", "second text" }, dto.GetProperty("input").EnumerateArray().Select(item => item.GetString()));
            Assert.False(dto.GetProperty("truncate").GetBoolean());
            Assert.Equal("5m", dto.GetProperty("keep_alive").GetString());
            Assert.False(dto.TryGetProperty("dimensions", out _));
            return Ok(new[] { 1f, 2f, 3f }, new[] { 4f, 5f, 6f });
        });
        using var client = new HttpClient(handler);
        var result = await Provider(client).EmbedAsync(chunks, Profile, CancellationToken.None);
        Assert.Equal(2, result.Count);
        Assert.Same(chunks[0], result[0].Chunk);
        Assert.Same(chunks[1], result[1].Chunk);
        Assert.Equal(new[] { 1f, 2f, 3f }, result[0].Vector);
        Assert.Equal(new[] { 4f, 5f, 6f }, result[1].Vector);
        Assert.Equal(3, result[0].Vector.Count);
    }

    [Fact]
    public async Task MultipleBatchesPreserveGlobalOrder()
    {
        var chunks = Enumerable.Range(0, 5).Select(index => Chunk(index.ToString(), "chunk-" + index)).ToArray();
        var nextVector = 0;
        var handler = new FakeHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var count = json.RootElement.GetProperty("input").GetArrayLength();
            var vectors = Enumerable.Range(nextVector, count).Select(index => new[] { (float)index, 0f, 0f }).ToArray();
            nextVector += count;
            return Ok(vectors);
        });
        using var client = new HttpClient(handler);
        var result = await Provider(client, batchSize: 2).EmbedAsync(chunks, Profile, CancellationToken.None);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(chunks, result.Select(item => item.Chunk));
        Assert.Equal(new[] { 0f, 1f, 2f, 3f, 4f }, result.Select(item => item.Vector[0]));
    }

    [Fact]
    public async Task EmptyInputReturnsWithoutHttpRequest()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected request."));
        using var client = new HttpClient(handler);
        var result = await Provider(client).EmbedAsync(Array.Empty<DocumentChunk>(), Profile, CancellationToken.None);
        Assert.Empty(result);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task UnsupportedProviderIsRejectedWithoutHttpRequest()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected request."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).EmbedAsync(new[] { Chunk("a", "private text") },
            new EmbeddingProfile("Other", "model", 3, false), CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ResponseCountMismatchThrowsClearInvalidDataException()
    {
        var handler = Responding(Ok(new[] { 1f, 2f, 3f }));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).EmbedAsync(
            new[] { Chunk("a", "one"), Chunk("b", "two") }, Profile, CancellationToken.None));
        Assert.Contains("1 embeddings for a batch of 2 inputs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseDimensionMismatchThrowsClearInvalidDataException()
    {
        using var client = new HttpClient(Responding(Ok(new[] { 1f, 2f })));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).EmbedAsync(
            new[] { Chunk("a", "one") }, Profile, CancellationToken.None));
        Assert.Contains("dimension 2; expected 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonFiniteValueIsRejected()
    {
        using var client = new HttpClient(Responding(JsonResponse("{\"embeddings\":[[1,1e1000,3]]}")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).EmbedAsync(
            new[] { Chunk("a", "secret chunk") }, Profile, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    public async Task MalformedOrEmptyJsonThrowsClearInvalidDataException(string body)
    {
        using var client = new HttpClient(Responding(JsonResponse(body)));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).EmbedAsync(
            new[] { Chunk("a", "text") }, Profile, CancellationToken.None));
        Assert.Contains("response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonSuccessStatusIncludesStatusButNotChunkTextOrVectorData()
    {
        const string secretText = "do not disclose this chunk text";
        using var client = new HttpClient(Responding(JsonResponse("error: " + secretText, HttpStatusCode.BadGateway)));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).EmbedAsync(
            new[] { Chunk("a", secretText) }, Profile, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Contains("502", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1,2,3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeHandler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).EmbedAsync(
            new[] { Chunk("a", "text") }, Profile, cancellation.Token));
    }

    [Theory]
    [InlineData("http://example.com/api/embed")]
    [InlineData("http://localhost/api/other")]
    [InlineData("http://localhost/api/embed?x=1")]
    [InlineData("http://user@localhost/api/embed")]
    public void OptionsRejectNonLocalOrUnsafeEndpoints(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new OllamaEmbeddingOptions { Endpoint = endpoint }.Validate());

    private static OllamaEmbeddingProvider Provider(HttpClient client, int batchSize = 8) =>
        new(client, new OllamaEmbeddingOptions { BatchSize = batchSize });
    private static DocumentChunk Chunk(string id, string text) => new(id, "doc", text, "Description", 0, 1);
    private static FakeHandler Responding(HttpResponseMessage response) => new((_, _) => Task.FromResult(response));
    private static HttpResponseMessage Ok(params float[][] vectors) => JsonResponse(JsonSerializer.Serialize(new { embeddings = vectors }));
    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

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
