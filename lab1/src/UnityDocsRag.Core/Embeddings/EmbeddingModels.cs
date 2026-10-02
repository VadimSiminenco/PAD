using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Core.Embeddings;

public sealed record EmbeddingProfile
{
    public EmbeddingProfile(string provider, string modelName, int dimension, bool multilingual)
    {
        Provider = string.IsNullOrWhiteSpace(provider)
            ? throw new ArgumentException("Provider must not be empty.", nameof(provider))
            : provider;
        ModelName = string.IsNullOrWhiteSpace(modelName)
            ? throw new ArgumentException("Model name must not be empty.", nameof(modelName))
            : modelName;
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), "Embedding dimension must be positive.");
        }

        Dimension = dimension;
        Multilingual = multilingual;
    }

    public string Provider { get; }
    public string ModelName { get; }
    public int Dimension { get; }
    public bool Multilingual { get; }
}

public sealed record ChunkEmbedding
{
    private readonly float[] _values;

    public ChunkEmbedding(DocumentChunk chunk, EmbeddingProfile profile, IReadOnlyList<float> values)
    {
        Chunk = chunk ?? throw new ArgumentNullException(nameof(chunk));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != profile.Dimension)
        {
            throw new ArgumentException(
                $"Embedding has {values.Count} values, but profile '{profile.ModelName}' requires {profile.Dimension}.",
                nameof(values));
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new ArgumentException(
                    $"Embedding value at index {index} must be finite; NaN and positive or negative infinity are not supported.",
                    nameof(values));
            }
        }

        _values = values.ToArray();
        Vector = Array.AsReadOnly(_values);
    }

    public DocumentChunk Chunk { get; }
    public EmbeddingProfile Profile { get; }
    public IReadOnlyList<float> Vector { get; }
}
