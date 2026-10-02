namespace UnityDocsRag.Core.Configuration;

public sealed class RagOptions
{
    public const string DefaultUnitySourceUrl = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/";

    public string UnitySourceUrl { get; init; } = DefaultUnitySourceUrl;
    public string UnityVersion { get; init; } = "6000.3";
    public EmbeddingOptions Embedding { get; init; } = new();
    public GenerationOptions Generation { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();
    public RetrievalOptions Retrieval { get; init; } = new();

    public void Validate()
    {
        if (!Uri.TryCreate(UnitySourceUrl, UriKind.Absolute, out var sourceUri) ||
            !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceUri.Host, "docs.unity3d.com", StringComparison.OrdinalIgnoreCase) ||
            !sourceUri.AbsolutePath.StartsWith("/6000.3/Documentation/ScriptReference/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Source URL must be an absolute HTTPS URL within the Unity 6000.3 Scripting API.",
                nameof(UnitySourceUrl));
        }

        if (string.IsNullOrWhiteSpace(UnityVersion))
        {
            throw new ArgumentException("Unity version must not be empty.", nameof(UnityVersion));
        }

        ArgumentNullException.ThrowIfNull(Embedding);
        ArgumentNullException.ThrowIfNull(Generation);
        ArgumentNullException.ThrowIfNull(Chunking);
        ArgumentNullException.ThrowIfNull(Retrieval);

        Embedding.Validate();
        Generation.Validate();
        Chunking.Validate();
        Retrieval.Validate();

        if (Retrieval.RerankerTopN > Retrieval.TopK)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Retrieval.RerankerTopN),
                "Reranker Top-N cannot be greater than retrieval Top-K.");
        }
    }
}

public sealed class EmbeddingOptions
{
    public string Provider { get; init; } = "Ollama";
    public string ModelName { get; init; } = "embeddinggemma";
    public int Dimension { get; init; } = 768;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new ArgumentException("Embedding provider must not be empty.", nameof(Provider));
        }

        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new ArgumentException("Embedding model name must not be empty.", nameof(ModelName));
        }

        if (Dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Dimension), "Embedding dimension must be positive.");
        }
    }
}

public sealed class GenerationOptions
{
    public string Provider { get; init; } = "Ollama";
    public string ModelName { get; init; } = "qwen3:4b";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new ArgumentException("Generation provider must not be empty.", nameof(Provider));
        }

        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new ArgumentException("Generation model name must not be empty.", nameof(ModelName));
        }
    }
}

public sealed class ChunkingOptions
{
    public int ChunkSize { get; init; } = 512;
    public int ChunkOverlap { get; init; } = 64;

    internal void Validate()
    {
        if (ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), "Chunk size must be positive.");
        }

        if (ChunkOverlap < 0 || ChunkOverlap >= ChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkOverlap), "Chunk overlap must be non-negative and less than chunk size.");
        }
    }
}

public sealed class RetrievalOptions
{
    public int TopK { get; init; } = 10;
    public int RerankerTopN { get; init; } = 5;
    public double SimilarityThreshold { get; init; } = 0.35;

    internal void Validate()
    {
        if (TopK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TopK), "Retrieval Top-K must be positive.");
        }

        if (RerankerTopN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RerankerTopN), "Reranker Top-N must be positive.");
        }

        if (!double.IsFinite(SimilarityThreshold) || SimilarityThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SimilarityThreshold), "Similarity threshold must be between 0 and 1.");
        }
    }
}
