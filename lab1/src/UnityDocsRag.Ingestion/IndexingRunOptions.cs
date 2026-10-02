using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Embeddings;
using UnityDocsRag.Infrastructure.Storage;

namespace UnityDocsRag.Ingestion;

public sealed class IndexingRunOptions
{
    public string ProcessedDocumentsPath { get; init; } = "data/processed/unity-6000.3/documents.json";
    public string ChunksPath { get; init; } = "data/chunks/unity-6000.3/chunks.json";
    public string EmbeddingProvider { get; init; } = "Ollama";
    public string EmbeddingModel { get; init; } = "embeddinggemma";
    public int EmbeddingDimension { get; init; } = 768;
    public bool Multilingual { get; init; } = true;
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434/api/embed";
    public int EmbeddingBatchSize { get; init; } = 8;
    public bool OllamaTruncate { get; init; }
    public string OllamaKeepAlive { get; init; } = "5m";
    public int OllamaRequestTimeoutSeconds { get; init; } = 300;
    public string PostgresConnectionStringEnvironmentVariable { get; init; } = "UNITYDOCS_POSTGRES_CONNECTION_STRING";
    public int PostgresCommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        RequireJsonPath(ProcessedDocumentsPath, nameof(ProcessedDocumentsPath));
        RequireJsonPath(ChunksPath, nameof(ChunksPath));
        if (string.Equals(Path.GetFullPath(ProcessedDocumentsPath), Path.GetFullPath(ChunksPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Processed documents and chunks must use different paths.");
        if (!string.Equals(EmbeddingProvider?.Trim(), "Ollama", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EmbeddingProvider must be 'Ollama'.", nameof(EmbeddingProvider));
        if (string.IsNullOrWhiteSpace(EmbeddingModel)) throw new ArgumentException("EmbeddingModel must not be empty.", nameof(EmbeddingModel));
        if (EmbeddingDimension <= 0 || EmbeddingDimension > 2000)
            throw new ArgumentOutOfRangeException(nameof(EmbeddingDimension), "Embedding dimension must be between 1 and 2000.");
        if (EmbeddingBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(EmbeddingBatchSize), "Embedding batch size must be positive.");
        if (OllamaRequestTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(OllamaRequestTimeoutSeconds), "Ollama request timeout must be positive.");
        if (PostgresCommandTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(PostgresCommandTimeoutSeconds), "PostgreSQL command timeout must be positive.");
        if (string.IsNullOrWhiteSpace(OllamaKeepAlive)) throw new ArgumentException("OllamaKeepAlive must not be empty.", nameof(OllamaKeepAlive));
        if (string.IsNullOrWhiteSpace(PostgresConnectionStringEnvironmentVariable) ||
            !IsValidEnvironmentVariableName(PostgresConnectionStringEnvironmentVariable))
            throw new ArgumentException("PostgreSQL connection string environment variable name is invalid.", nameof(PostgresConnectionStringEnvironmentVariable));
        CreateOllamaOptions().Validate();
    }

    public EmbeddingProfile CreateEmbeddingProfile() =>
        new(EmbeddingProvider.Trim(), EmbeddingModel.Trim(), EmbeddingDimension, Multilingual);

    public OllamaEmbeddingOptions CreateOllamaOptions() => new()
    {
        Endpoint = OllamaEndpoint,
        BatchSize = EmbeddingBatchSize,
        Truncate = OllamaTruncate,
        KeepAlive = OllamaKeepAlive
    };

    public string GetPostgresConnectionString()
    {
        if (string.IsNullOrWhiteSpace(PostgresConnectionStringEnvironmentVariable) ||
            !IsValidEnvironmentVariableName(PostgresConnectionStringEnvironmentVariable))
            throw new InvalidOperationException("PostgreSQL connection string environment variable name is invalid.");
        var value = Environment.GetEnvironmentVariable(PostgresConnectionStringEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Required environment variable '{PostgresConnectionStringEnvironmentVariable}' is not set.");
    }

    private static void RequireJsonPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path must be a non-empty .json file path.", name);
    }

    private static bool IsValidEnvironmentVariableName(string name) =>
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
