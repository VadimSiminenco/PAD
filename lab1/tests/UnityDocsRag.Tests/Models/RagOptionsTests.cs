using UnityDocsRag.Core.Configuration;

namespace UnityDocsRag.Tests.Models;

public sealed class RagOptionsTests
{
    [Fact]
    public void DefaultsAreValidAndMatchSelectedLocalModels()
    {
        var options = new RagOptions();

        options.Validate();

        Assert.Equal("6000.3", options.UnityVersion);
        Assert.Equal("embeddinggemma", options.Embedding.ModelName);
        Assert.Equal(768, options.Embedding.Dimension);
        Assert.Equal("qwen3:4b", options.Generation.ModelName);
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(512, 600)]
    public void ValidateRejectsOverlapAtLeastChunkSize(int size, int overlap)
    {
        var options = new RagOptions { Chunking = new ChunkingOptions { ChunkSize = size, ChunkOverlap = overlap } };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsNonPositiveEmbeddingDimension()
    {
        var options = new RagOptions { Embedding = new EmbeddingOptions { Dimension = 0 } };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData("https://docs.unity3d.com/6000.3/Documentation/Manual/index.html")]
    [InlineData("https://example.com/6000.3/Documentation/ScriptReference/")]
    [InlineData("/6000.3/Documentation/ScriptReference/")]
    public void ValidateRejectsSourceOutsideUnityScriptingApi(string sourceUrl)
    {
        var options = new RagOptions { UnitySourceUrl = sourceUrl };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsRerankerTopNGreaterThanTopK()
    {
        var options = new RagOptions
        {
            Retrieval = new RetrievalOptions { TopK = 4, RerankerTopN = 5 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsInvalidSimilarityThreshold()
    {
        var options = new RagOptions { Retrieval = new RetrievalOptions { SimilarityThreshold = double.NaN } };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
