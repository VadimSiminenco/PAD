using UnityDocsRag.Core.Embeddings;

namespace UnityDocsRag.Infrastructure.Storage;

public static class EmbeddingProfileKey
{
    public static string Create(EmbeddingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var provider = profile.Provider.Trim().ToLowerInvariant();
        var model = profile.ModelName.Trim().ToLowerInvariant();
        return $"{provider}:{model}:{profile.Dimension}";
    }
}
