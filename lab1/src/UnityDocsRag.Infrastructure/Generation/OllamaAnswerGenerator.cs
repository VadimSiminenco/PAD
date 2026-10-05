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
                description = "A meaningful, source-grounded answer covering every part of a multi-part question, including each compared operation and requested effect or condition; explicitly identify unsupported parts. A method name alone is acceptable only when asked solely which method. A SOURCE number or bare yes/no cannot answer an operation or comparison. Put SOURCE numbers only in citedSourceNumbers and include every SOURCE actually used. Must be non-empty when sufficientEvidence is true."
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
    private const string RetryClarification = "Your previous answer was only a source number or a bare yes/no. Give a meaningful answer in the answer field; put SOURCE numbers only in citedSourceNumbers. Follow all original evidence and language rules.";
    private const string StructuredJsonRetryClarification = "Return exactly one valid JSON object matching the requested schema, with no Markdown or extra text. Keep the answer brief and substantive.";

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
        string? retryClarification = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParsedAnswer parsed;
            try
            {
                parsed = await RequestAnswerAsync(prompt, evidence.Count, attempt + 1,
                        retryClarification, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OllamaAnswerValidationException exception) when
                (attempt == 0 && exception.ReasonCode == OllamaAnswerValidationCode.StructuredJsonMalformed)
            {
                retryClarification = StructuredJsonRetryClarification;
                continue;
            }

            if (!parsed.SufficientEvidence) return InsufficientEvidence(language);
            if (IsClearlyUninformativeAnswer(parsed.Answer))
            {
                if (attempt == 0)
                {
                    retryClarification = RetryClarification;
                    continue;
                }
                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.NonSubstantiveAnswer, attempt + 1);
            }

            var citations = parsed.CitedSourceNumbers
                .Select(sourceNumber => evidence[sourceNumber - 1])
                .Select(source => new Citation(source.SourceTitle, source.SourceUrl, source.Chunk.Section))
                .ToArray();
            return new RagAnswer(parsed.Answer, language, AnswerStatus.Answered, citations);
        }

        throw new InvalidOperationException("Generation attempts were exhausted.");
    }

    private async Task<ParsedAnswer> RequestAnswerAsync(RagPrompt prompt, int sourceCount, int attemptNumber,
        string? clarification, CancellationToken cancellationToken)
    {
        var requestBody = new ChatRequest(
            _options.Model,
            new[]
            {
                new ChatMessage("system", clarification is null ? prompt.SystemMessage : prompt.SystemMessage + "\n" + clarification),
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
            throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.OuterJsonMalformed, attemptNumber);
        }

        var content = outer?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.MessageContentMissing, attemptNumber);
        return ParseStructuredAnswer(content, sourceCount, attemptNumber);
    }

    private static bool IsClearlyUninformativeAnswer(string answer) =>
        Regex.IsMatch(answer, @"^\s*(?:(?:source\s*#?\s*)?\d+|(?:source\s*)?\[\d+\])\s*[.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        Regex.IsMatch(answer, @"^\s*(?:да|нет|yes|no)\s*[.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static ParsedAnswer ParseStructuredAnswer(string content, int sourceCount, int attemptNumber)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.StructuredJsonMalformed, attemptNumber);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.StructuredShapeInvalid, attemptNumber);

            bool? sufficientEvidence = null;
            string? answer = null;
            List<int>? sourceNumbers = null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                    throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.StructuredShapeInvalid, attemptNumber);
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
                            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var sourceNumber) ||
                                sourceNumber < 1 || sourceNumber > sourceCount)
                                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.CitationNumberInvalid, attemptNumber);
                            if (!unique.Add(sourceNumber))
                                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.CitationNumberDuplicate, attemptNumber);
                            sourceNumbers.Add(sourceNumber);
                        }
                        break;
                    default:
                        throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.StructuredShapeInvalid, attemptNumber);
                }
            }

            if (fields.Count != 3 || sufficientEvidence is null || answer is null || sourceNumbers is null)
                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.StructuredShapeInvalid, attemptNumber);
            if (sufficientEvidence.Value && string.IsNullOrWhiteSpace(answer))
                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.AnswerMissing, attemptNumber);
            if (sufficientEvidence.Value && Regex.IsMatch(answer!, @"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.AnswerUrlForbidden, attemptNumber);
            if (sufficientEvidence.Value && sourceNumbers.Count == 0)
                throw new OllamaAnswerValidationException(OllamaAnswerValidationCode.CitationMissing, attemptNumber);

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

public enum OllamaAnswerValidationCode
{
    OuterJsonMalformed,
    MessageContentMissing,
    StructuredJsonMalformed,
    StructuredShapeInvalid,
    CitationNumberInvalid,
    CitationNumberDuplicate,
    AnswerMissing,
    AnswerUrlForbidden,
    CitationMissing,
    NonSubstantiveAnswer
}

public sealed class OllamaAnswerValidationException : Exception
{
    public OllamaAnswerValidationCode ReasonCode { get; }
    public int AttemptNumber { get; }

    internal OllamaAnswerValidationException(OllamaAnswerValidationCode reasonCode, int attemptNumber)
        : base("Ollama answer validation failed.")
    {
        ReasonCode = reasonCode;
        AttemptNumber = attemptNumber;
    }
}
