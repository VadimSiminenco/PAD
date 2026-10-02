using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Embeddings;

namespace UnityDocsRag.Ingestion;

public sealed class SearchRunOptions
{
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";
    public string EmbeddingProvider { get; init; } = "Ollama";
    public string EmbeddingModel { get; init; } = "embeddinggemma";
    public int EmbeddingDimension { get; init; } = 768;
    public bool Multilingual { get; init; } = true;
    public bool OllamaTruncate { get; init; }
    public string OllamaKeepAlive { get; init; } = "5m";
    public string PostgresConnectionStringEnvironmentVariable { get; init; } = "UNITYDOCS_POSTGRES_CONNECTION_STRING";
    public int PostgresCommandTimeoutSeconds { get; init; } = 30;
    public int TopK { get; init; } = 5;
    public double SimilarityThreshold { get; init; }

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
        if (string.IsNullOrWhiteSpace(OllamaKeepAlive)) throw new ArgumentException("OllamaKeepAlive must not be empty.", nameof(OllamaKeepAlive));
        if (PostgresCommandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(PostgresCommandTimeoutSeconds), "PostgreSQL command timeout must be positive.");
        if (TopK <= 0) throw new ArgumentOutOfRangeException(nameof(TopK), "TopK must be positive.");
        if (!double.IsFinite(SimilarityThreshold) || SimilarityThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(SimilarityThreshold), "Similarity threshold must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(PostgresConnectionStringEnvironmentVariable) ||
            !IsValidEnvironmentVariableName(PostgresConnectionStringEnvironmentVariable))
            throw new ArgumentException("PostgreSQL connection string environment variable name is invalid.", nameof(PostgresConnectionStringEnvironmentVariable));
        CreateOllamaOptions().Validate();
    }

    public EmbeddingProfile CreateEmbeddingProfile() =>
        new(EmbeddingProvider.Trim(), EmbeddingModel.Trim(), EmbeddingDimension, Multilingual);

    public OllamaEmbeddingOptions CreateOllamaOptions() => new()
    {
        Endpoint = OllamaEndpoint.TrimEnd('/') + "/api/embed",
        BatchSize = 1,
        Truncate = OllamaTruncate,
        KeepAlive = OllamaKeepAlive
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

    private static bool IsValidEnvironmentVariableName(string name) =>
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
