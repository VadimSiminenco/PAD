using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Core.Retrieval;

namespace UnityDocsRag.Infrastructure.Generation;

public sealed class OllamaAnswerGenerator : IAnswerGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly object ResponseSchema = new
    {
        type = "object",
        properties = new
        {
            sufficientEvidence = new
            {
                type = "boolean",
                description = "True when a supplied source directly supports a useful, correct answer; false only when none directly answer the question."
            },
            answer = new
            {
                type = "string",
                description = "A concise answer using only facts supported by the supplied sources; must be non-empty when sufficientEvidence is true."
            },
            citedSourceNumbers = new
            {
                type = "array",
                description = "When sufficientEvidence is true, include the numbers of the supplied SOURCE records actually used to support the answer.",
                items = new { type = "integer", minimum = 1 },
                uniqueItems = true
            }
        },
        required = new[] { "sufficientEvidence", "answer", "citedSourceNumbers" },
        additionalProperties = false
    };

    private readonly HttpClient _httpClient;
    private readonly OllamaGenerationOptions _options;
    private readonly Uri _endpoint;
    private readonly RagPromptBuilder _promptBuilder;

    public OllamaAnswerGenerator(HttpClient httpClient, OllamaGenerationOptions options, RagPromptBuilder? promptBuilder = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _endpoint = _options.CreateChatUri();
        _promptBuilder = promptBuilder ?? new RagPromptBuilder();
    }

    public async Task<RagAnswer> GenerateAsync(UserQuestion question, SupportedLanguage language,
        IReadOnlyList<RetrievedChunk> evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language), "Only Russian and English are supported.");
        if (evidence.Any(chunk => chunk is null)) throw new ArgumentException("Evidence cannot contain null chunks.", nameof(evidence));
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.Count == 0) return InsufficientEvidence(language);

        var prompt = _promptBuilder.Build(question, language, evidence);
        var requestBody = new ChatRequest(
            _options.Model,
            new[]
            {
                new ChatMessage("system", prompt.SystemMessage),
                new ChatMessage("user", prompt.UserMessage)
            },
            Stream: false,
            KeepAlive: _options.KeepAlive,
            Think: false,
            Format: ResponseSchema,
            Options: new GenerationSettings(_options.Temperature, _options.NumPredict, _options.NumCtx));
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ollama chat request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null, response.StatusCode);

        var outerJson = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        OllamaChatResponse? outer;
        try
        {
            outer = JsonSerializer.Deserialize<OllamaChatResponse>(outerJson, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Ollama chat response contains malformed JSON.");
        }

        var content = outer?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("Ollama chat response is missing message content.");
        var parsed = ParseStructuredAnswer(content, evidence.Count);
        if (!parsed.SufficientEvidence) return InsufficientEvidence(language);

        var citations = parsed.CitedSourceNumbers
            .Select(sourceNumber => evidence[sourceNumber - 1])
            .Select(source => new Citation(source.SourceTitle, source.SourceUrl, source.Chunk.Section))
            .ToArray();
        return new RagAnswer(parsed.Answer, language, AnswerStatus.Answered, citations);
    }

    private static ParsedAnswer ParseStructuredAnswer(string content, int sourceCount)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Ollama message content contains malformed structured JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Structured answer must be a JSON object.");

            bool? sufficientEvidence = null;
            string? answer = null;
            List<int>? sourceNumbers = null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw new InvalidDataException("Structured answer contains a duplicate field.");
                switch (property.Name)
                {
                    case "sufficientEvidence" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        sufficientEvidence = property.Value.GetBoolean();
                        break;
                    case "answer" when property.Value.ValueKind == JsonValueKind.String:
                        answer = property.Value.GetString();
                        break;
                    case "citedSourceNumbers" when property.Value.ValueKind == JsonValueKind.Array:
                        sourceNumbers = new List<int>();
                        var unique = new HashSet<int>();
                        foreach (var value in property.Value.EnumerateArray())
                        {
                            if (!value.TryGetInt32(out var sourceNumber) || sourceNumber < 1 || sourceNumber > sourceCount)
                                throw new InvalidDataException("Structured answer contains a source number outside the supplied context.");
                            if (!unique.Add(sourceNumber))
                                throw new InvalidDataException("Structured answer contains duplicate source numbers.");
                            sourceNumbers.Add(sourceNumber);
                        }
                        break;
                    default:
                        throw new InvalidDataException("Structured answer contains an unsupported or invalid field.");
                }
            }

            if (fields.Count != 3 || sufficientEvidence is null || answer is null || sourceNumbers is null)
                throw new InvalidDataException("Structured answer is missing a required field.");
            if (sufficientEvidence.Value && string.IsNullOrWhiteSpace(answer))
                throw new InvalidDataException("An answer with sufficient evidence must contain answer text.");
            if (sufficientEvidence.Value && Regex.IsMatch(answer!, @"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Answer text must not contain model-generated URLs.");
            if (sufficientEvidence.Value && sourceNumbers.Count == 0)
                throw new InvalidDataException("An answer with sufficient evidence must cite at least one source.");

            return new ParsedAnswer(sufficientEvidence.Value, answer, sourceNumbers);
        }
    }

    private static RagAnswer InsufficientEvidence(SupportedLanguage language) => language switch
    {
        SupportedLanguage.Russian => new RagAnswer(
            "В предоставленной документации недостаточно информации для ответа.", language, AnswerStatus.InsufficientEvidence),
        SupportedLanguage.English => new RagAnswer(
            "The provided documentation does not contain enough information to answer.", language, AnswerStatus.InsufficientEvidence),
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

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

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; init; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed record ParsedAnswer(bool SufficientEvidence, string Answer, IReadOnlyList<int> CitedSourceNumbers);
}
