using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Infrastructure.Reranking;

public sealed class OllamaReranker : IReranker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object ResponseSchema = new
    {
        type = "object",
        properties = new
        {
            rankedSourceNumbers = new
            {
                type = "array",
                description = "A complete ordering of all supplied SOURCE numbers, best first, each exactly once.",
                items = new { type = "integer", minimum = 1 },
                uniqueItems = true
            }
        },
        required = new[] { "rankedSourceNumbers" },
        additionalProperties = false
    };

    private readonly HttpClient _httpClient;
    private readonly OllamaRerankerOptions _options;
    private readonly Uri _endpoint;
    private readonly RagRerankPromptBuilder _promptBuilder;

    public OllamaReranker(HttpClient httpClient, OllamaRerankerOptions options, RagRerankPromptBuilder? promptBuilder = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _endpoint = _options.CreateChatUri();
        _promptBuilder = promptBuilder ?? new RagRerankPromptBuilder();
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        UserQuestion question,
        IReadOnlyList<RetrievedChunk> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Any(candidate => candidate is null))
            throw new ArgumentException("Candidates cannot contain null values.", nameof(candidates));
        if (candidates.Count > _options.MaxCandidates)
            throw new ArgumentException("Candidate count exceeds the configured maximum.", nameof(candidates));
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return Array.AsReadOnly(Array.Empty<RetrievedChunk>());

        var prompt = _promptBuilder.Build(question, candidates);
        var requestBody = new ChatRequest(
            _options.Model,
            new[] { new ChatMessage("system", prompt.SystemMessage), new ChatMessage("user", prompt.UserMessage) },
            Stream: false,
            KeepAlive: _options.KeepAlive,
            Think: _options.Think,
            Format: ResponseSchema,
            Options: new GenerationSettings(_options.Temperature, _options.NumPredict, _options.NumCtx));
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new HttpRequestException("Ollama reranker HTTP request failed.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Ollama reranker request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                    null,
                    response.StatusCode);

            string outerJson;
            try
            {
                outerJson = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw new InvalidDataException("Ollama reranker response could not be read.");
            }

            var content = ReadMessageContent(outerJson);
            var sourceNumbers = ParseRankedSourceNumbers(content, candidates.Count);
            var results = new RetrievedChunk[sourceNumbers.Length];
            for (var index = 0; index < sourceNumbers.Length; index++)
            {
                var candidate = candidates[sourceNumbers[index] - 1];
                var rank = index + 1;
                var score = sourceNumbers.Length == 1 ? 1.0 : 1.0 - (double)index / (sourceNumbers.Length - 1);
                results[index] = new RetrievedChunk(candidate.Chunk, candidate.SourceUrl, candidate.SourceTitle,
                    candidate.SimilarityScore, candidate.InitialRank, score, rank);
            }

            return Array.AsReadOnly(results);
        }
    }

    private static string ReadMessageContent(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Ollama reranker returned malformed outer JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ollama reranker outer response must be a JSON object.");

            JsonElement message = default;
            var messageCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "message", StringComparison.Ordinal)) continue;
                message = property.Value;
                messageCount++;
            }

            if (messageCount != 1 || message.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ollama reranker response is missing a valid message object.");

            JsonElement content = default;
            var contentCount = 0;
            foreach (var property in message.EnumerateObject())
            {
                if (!string.Equals(property.Name, "content", StringComparison.Ordinal)) continue;
                content = property.Value;
                contentCount++;
            }

            if (contentCount != 1 || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
                throw new InvalidDataException("Ollama reranker response is missing message content.");
            return content.GetString()!;
        }
    }

    private static int[] ParseRankedSourceNumbers(string json, int candidateCount)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Ollama reranker message content contains malformed structured JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Reranker output must be a JSON object.");

            JsonElement rankedNumbers = default;
            var propertyCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                propertyCount++;
                if (string.Equals(property.Name, "rankedSourceNumbers", StringComparison.Ordinal))
                    rankedNumbers = property.Value;
                else
                    throw new InvalidDataException("Reranker output contains an unsupported field.");
            }

            if (propertyCount != 1 || rankedNumbers.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Reranker output must contain exactly one rankedSourceNumbers array.");

            var result = new List<int>(candidateCount);
            var seen = new bool[candidateCount + 1];
            foreach (var value in rankedNumbers.EnumerateArray())
            {
                if (!value.TryGetInt32(out var number) || number < 1 || number > candidateCount)
                    throw new InvalidDataException("Reranker output contains a source number outside the candidate range.");
                if (seen[number]) throw new InvalidDataException("Reranker output contains a duplicate source number.");
                seen[number] = true;
                result.Add(number);
            }

            if (result.Count != candidateCount || seen.Skip(1).Any(present => !present))
                throw new InvalidDataException("Reranker output must be a complete permutation of all candidate source numbers.");
            return result.ToArray();
        }
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        [property: JsonPropertyName("think")] bool Think,
        [property: JsonPropertyName("format")] object Format,
        [property: JsonPropertyName("options")] GenerationSettings Options);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record GenerationSettings(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_predict")] int NumPredict,
        [property: JsonPropertyName("num_ctx")] int NumCtx);
}
