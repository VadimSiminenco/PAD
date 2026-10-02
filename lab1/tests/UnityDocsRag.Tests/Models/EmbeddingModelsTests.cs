using UnityDocsRag.Core.Documents;
using UnityDocsRag.Core.Embeddings;

namespace UnityDocsRag.Tests.Models;

public sealed class EmbeddingModelsTests
{
    [Fact]
    public void ChunkEmbeddingAcceptsFiniteVectorMatchingProfileDimensionAndCopiesIt()
    {
        var chunk = new DocumentChunk("chunk-1", "doc-1", "text", null, 0, 1);
        var profile = new EmbeddingProfile("local", "test-model", 3, multilingual: true);
        var values = new List<float> { 0.1f, 0.2f, 0.3f };
        var embedding = new ChunkEmbedding(chunk, profile, values);
        values[0] = 0.9f;

        Assert.Equal(3, embedding.Vector.Count);
        Assert.Equal(0.1f, embedding.Vector[0]);
        Assert.True(embedding.Profile.Multilingual);
    }

    [Fact]
    public void ChunkEmbeddingRejectsVectorWithWrongDimension()
    {
        var chunk = new DocumentChunk("chunk-1", "doc-1", "text", null, 0, 1);
        var profile = new EmbeddingProfile("local", "test-model", 3, multilingual: false);

        Assert.Throws<ArgumentException>(() => new ChunkEmbedding(chunk, profile, new float[] { 0.1f, 0.2f }));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ChunkEmbeddingRejectsNonFiniteValues(float value)
    {
        var chunk = new DocumentChunk("chunk-1", "doc-1", "text", null, 0, 1);
        var profile = new EmbeddingProfile("local", "test-model", 2, multilingual: false);

        Assert.Throws<ArgumentException>(() => new ChunkEmbedding(chunk, profile, new float[] { 0.1f, value }));
    }
}
