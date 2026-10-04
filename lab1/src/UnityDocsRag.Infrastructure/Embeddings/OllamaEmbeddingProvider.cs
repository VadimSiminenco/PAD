using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;

namespace UnityDocsRag.Infrastructure.Embeddings;

public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IQueryEmbeddingProvider
{
    private const int ErrorBodyLimit = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly OllamaEmbeddingOptions _options;
    private readonly Uri _endpoint;

    public OllamaEmbeddingProvider(HttpClient httpClient, OllamaEmbeddingOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _endpoint = new Uri(_options.Endpoint, UriKind.Absolute);
    }

    public async Task<IReadOnlyList<ChunkEmbedding>> EmbedAsync(
        IReadOnlyList<DocumentChunk> chunks,
        EmbeddingProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ValidateProfile(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (chunks.Count == 0) return Array.AsReadOnly(Array.Empty<ChunkEmbedding>());

        var originalTexts = chunks.Select(chunk => chunk.Text).ToArray();
        var inputTexts = IsEmbeddingGemma(profile.ModelName)
            ? originalTexts.Select(FormatEmbeddingGemmaDocumentInput).ToArray()
            : originalTexts;
        var vectors = await EmbedTextsAsync(inputTexts, originalTexts, profile, cancellationToken).ConfigureAwait(false);
        var result = new List<ChunkEmbedding>(chunks.Count);
        for (var index = 0; index < vectors.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new ChunkEmbedding(chunks[index], profile, vectors[index]));
        }
        return result.AsReadOnly();
    }

    public async Task<QueryEmbedding> EmbedQueryAsync(string question, EmbeddingProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question must not be empty or whitespace.", nameof(question));
        ValidateProfile(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var input = IsEmbeddingGemma(profile.ModelName) ? FormatEmbeddingGemmaQueryInput(question) : question;
        var vectors = await EmbedTextsAsync(new[] { input }, new[] { question }, profile, cancellationToken).ConfigureAwait(false);
        if (vectors.Count != 1)
            throw new InvalidDataException($"Ollama returned {vectors.Count} embeddings for a single query input.");
        return new QueryEmbedding(profile, vectors[0]);
    }

    private async Task<IReadOnlyList<float[]>> EmbedTextsAsync(IReadOnlyList<string> inputTexts,
        IReadOnlyList<string> originalTexts, EmbeddingProfile profile, CancellationToken cancellationToken)
    {
        if (inputTexts.Count == 0) return Array.AsReadOnly(Array.Empty<float[]>());
        var result = new List<float[]>(inputTexts.Count);
        for (var offset = 0; offset < inputTexts.Count; offset += _options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(_options.BatchSize, inputTexts.Count - offset);
            var batch = new string[count];
            var originalBatch = new string[count];
            for (var index = 0; index < count; index++)
            {
                batch[index] = inputTexts[offset + index];
                originalBatch[index] = originalTexts[offset + index];
            }
            var requestDto = new EmbedRequest(profile.ModelName, batch, _options.Truncate, _options.KeepAlive);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(requestDto, options: JsonOptions)
            };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var safeBody = LimitAndRedact(body, batch.Concat(originalBatch).ToArray());
                throw new HttpRequestException(
                    $"Ollama embedding request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). Response: {safeBody}",
                    null, response.StatusCode);
            }

            EmbedResponse? responseDto;
            try
            {
                if (string.IsNullOrWhiteSpace(body)) throw new JsonException("Response body is empty.");
                responseDto = JsonSerializer.Deserialize<EmbedResponse>(body, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Ollama embedding response is empty or contains invalid JSON.", exception);
            }

            var vectors = responseDto?.Embeddings;
            if (vectors is null) throw new InvalidDataException("Ollama embedding response is missing the embeddings array.");
            if (vectors.Length != batch.Length)
                throw new InvalidDataException($"Ollama returned {vectors.Length} embeddings for a batch of {batch.Length} inputs.");
            for (var index = 0; index < vectors.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = vectors[index];
                if (vector is null) throw new InvalidDataException($"Ollama returned a null embedding at batch index {index}.");
                if (vector.Length != profile.Dimension)
                    throw new InvalidDataException($"Ollama embedding at batch index {index} has dimension {vector.Length}; expected {profile.Dimension}.");
                for (var valueIndex = 0; valueIndex < vector.Length; valueIndex++)
                {
                    if (!float.IsFinite(vector[valueIndex]))
                        throw new InvalidDataException($"Ollama embedding at batch index {index} contains a non-finite value at position {valueIndex}.");
                }
                result.Add(vector);
            }
        }
        return result.AsReadOnly();
    }

    private static void ValidateProfile(EmbeddingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Embedding profile provider must be 'Ollama'.", nameof(profile));
    }

    private static bool IsEmbeddingGemma(string modelName)
    {
        var normalized = modelName.Trim();
        return string.Equals(normalized, "embeddinggemma", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("embeddinggemma:", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEmbeddingGemmaDocumentInput(string text) => "title: none | text: " + text;

    private static string FormatEmbeddingGemmaQueryInput(string question) => "task: search result | query: " + question;

    private static string LimitAndRedact(string body, IReadOnlyList<string> inputTexts)
    {
        var safe = body;
        foreach (var inputText in inputTexts)
        {
            if (!string.IsNullOrEmpty(inputText)) safe = safe.Replace(inputText, "[input text redacted]", StringComparison.Ordinal);
        }
        safe = safe.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safe.Length > ErrorBodyLimit) safe = safe[..ErrorBodyLimit] + "…";
        return safe.Length == 0 ? "<empty response body>" : safe;
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("truncate")] bool Truncate,
        [property: JsonPropertyName("keep_alive")] string KeepAlive);

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public float[][]? Embeddings { get; init; }
    }
}
