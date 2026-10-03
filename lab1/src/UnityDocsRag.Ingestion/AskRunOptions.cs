using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Embeddings;
using UnityDocsRag.Infrastructure.Generation;

namespace UnityDocsRag.Ingestion;

public sealed class AskRunOptions
{
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";
    public string EmbeddingProvider { get; init; } = "Ollama";
    public string EmbeddingModel { get; init; } = "embeddinggemma";
    public int EmbeddingDimension { get; init; } = 768;
    public bool Multilingual { get; init; } = true;
    public bool OllamaTruncate { get; init; }
    public string EmbeddingKeepAlive { get; init; } = "5m";
    public string GenerationModel { get; init; } = "qwen3:4b";
    public string GenerationKeepAlive { get; init; } = "5m";
    public int GenerationTimeoutSeconds { get; init; } = 1200;
    public double Temperature { get; init; }
    public int NumPredict { get; init; } = 256;
    public int NumCtx { get; init; } = 4096;
    public string PostgresConnectionStringEnvironmentVariable { get; init; } = "UNITYDOCS_POSTGRES_CONNECTION_STRING";
    public int PostgresCommandTimeoutSeconds { get; init; } = 30;
    public int TopK { get; init; } = 5;
    public double DomainSimilarityThreshold { get; init; } = 0.25;
    public double EvidenceSimilarityThreshold { get; init; } = 0.45;
    public int MaxEvidenceChunks { get; init; } = 3;

    public void Validate()
    {
        if (!Uri.TryCreate(OllamaEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            (!string.Equals(endpoint.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Host.Trim('[', ']'), "::1", StringComparison.OrdinalIgnoreCase)) ||
            endpoint.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Ollama endpoint must be a loopback HTTP(S) origin.", nameof(OllamaEndpoint));
        if (!string.Equals(EmbeddingProvider?.Trim(), "Ollama", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EmbeddingProvider must be 'Ollama'.", nameof(EmbeddingProvider));
        if (string.IsNullOrWhiteSpace(EmbeddingModel)) throw new ArgumentException("EmbeddingModel must not be empty.", nameof(EmbeddingModel));
        if (EmbeddingDimension is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(EmbeddingDimension), "Embedding dimension must be between 1 and 2000.");
        if (string.IsNullOrWhiteSpace(EmbeddingKeepAlive)) throw new ArgumentException("EmbeddingKeepAlive must not be empty.", nameof(EmbeddingKeepAlive));
        if (string.IsNullOrWhiteSpace(GenerationModel)) throw new ArgumentException("GenerationModel must not be empty.", nameof(GenerationModel));
        if (string.IsNullOrWhiteSpace(GenerationKeepAlive)) throw new ArgumentException("GenerationKeepAlive must not be empty.", nameof(GenerationKeepAlive));
        if (PostgresCommandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(PostgresCommandTimeoutSeconds), "PostgreSQL command timeout must be positive.");
        if (string.IsNullOrWhiteSpace(PostgresConnectionStringEnvironmentVariable) ||
            !IsValidEnvironmentVariableName(PostgresConnectionStringEnvironmentVariable))
            throw new ArgumentException("PostgreSQL connection string environment variable name is invalid.", nameof(PostgresConnectionStringEnvironmentVariable));
        if (TopK <= 0) throw new ArgumentOutOfRangeException(nameof(TopK), "TopK must be positive.");
        if (MaxEvidenceChunks <= 0 || MaxEvidenceChunks > TopK)
            throw new ArgumentOutOfRangeException(nameof(MaxEvidenceChunks), "MaxEvidenceChunks must be positive and no greater than TopK.");
        ValidateThreshold(DomainSimilarityThreshold, nameof(DomainSimilarityThreshold));
        ValidateThreshold(EvidenceSimilarityThreshold, nameof(EvidenceSimilarityThreshold));
        if (DomainSimilarityThreshold >= EvidenceSimilarityThreshold)
            throw new ArgumentException("Domain threshold must be lower than evidence threshold.", nameof(DomainSimilarityThreshold));

        CreateOllamaEmbeddingOptions().Validate();
        CreateOllamaGenerationOptions().Validate();
    }

    public EmbeddingProfile CreateEmbeddingProfile() =>
        new(EmbeddingProvider.Trim(), EmbeddingModel.Trim(), EmbeddingDimension, Multilingual);

    public OllamaEmbeddingOptions CreateOllamaEmbeddingOptions() => new()
    {
        Endpoint = OllamaEndpoint.TrimEnd('/') + "/api/embed",
        BatchSize = 1,
        Truncate = OllamaTruncate,
        KeepAlive = EmbeddingKeepAlive
    };

    public OllamaGenerationOptions CreateOllamaGenerationOptions() => new()
    {
        Endpoint = OllamaEndpoint,
        Model = GenerationModel,
        KeepAlive = GenerationKeepAlive,
        HttpTimeoutSeconds = GenerationTimeoutSeconds,
        Temperature = Temperature,
        NumPredict = NumPredict,
        NumCtx = NumCtx
    };

    public string GetPostgresConnectionString()
    {
        if (string.IsNullOrWhiteSpace(PostgresConnectionStringEnvironmentVariable) ||
            !IsValidEnvironmentVariableName(PostgresConnectionStringEnvironmentVariable))
            throw new InvalidOperationException("PostgreSQL connection string environment variable name is invalid.");
        var connectionString = Environment.GetEnvironmentVariable(PostgresConnectionStringEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new InvalidOperationException($"Required environment variable '{PostgresConnectionStringEnvironmentVariable}' is not set.");
    }

    private static void ValidateThreshold(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "Similarity threshold must be finite and between 0 and 1.");
    }

    private static bool IsValidEnvironmentVariableName(string name) =>
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
