using UnityDocsRag.Core.Embeddings;
using UnityDocsRag.Infrastructure.Storage;

namespace UnityDocsRag.Tests.Storage;

public sealed class EmbeddingProfileKeyTests
{
    [Fact]
    public void CurrentOllamaEmbeddingGemmaProfileHasStableKey() =>
        Assert.Equal("ollama:embeddinggemma:768", EmbeddingProfileKey.Create(new EmbeddingProfile("Ollama", "embeddinggemma", 768, true)));

    [Fact]
    public void TrimsAndNormalizesProviderAndModelCase() =>
        Assert.Equal("ollama:embeddinggemma:768", EmbeddingProfileKey.Create(new EmbeddingProfile("  OLLAMA ", " EmbeddingGemma  ", 768, true)));

    [Fact]
    public void DifferentModelsOrDimensionsProduceDifferentKeys()
    {
        var first = EmbeddingProfileKey.Create(new EmbeddingProfile("Ollama", "embeddinggemma", 768, true));
        Assert.NotEqual(first, EmbeddingProfileKey.Create(new EmbeddingProfile("Ollama", "other-model", 768, true)));
        Assert.NotEqual(first, EmbeddingProfileKey.Create(new EmbeddingProfile("Ollama", "embeddinggemma", 1024, true)));
    }

    [Fact]
    public void NullProfileIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => EmbeddingProfileKey.Create(null!));
}
