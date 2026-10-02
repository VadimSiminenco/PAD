using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Tests.Models;

public sealed class DocumentModelsTests
{
    private static readonly Uri UnityUrl = new("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/GameObject.html");

    [Fact]
    public void RetrievedAndProcessedDocumentsPreserveIdentityAndContent()
    {
        var retrieved = new RetrievedDocument("game-object", UnityUrl, "GameObject", "6000.3", "raw", "hash", DateTimeOffset.UtcNow);
        var processed = new ProcessedDocument("game-object", UnityUrl, "GameObject", "6000.3", "clean", "hash", retrieved.RetrievedAt);

        Assert.Equal("raw", retrieved.Content);
        Assert.Equal("clean", processed.Content);
        Assert.Equal(retrieved.DocumentId, processed.DocumentId);
        Assert.Equal(UnityUrl, processed.CanonicalUrl);
    }

    [Theory]
    [InlineData("documentId")]
    [InlineData("title")]
    [InlineData("unityVersion")]
    [InlineData("content")]
    [InlineData("contentHash")]
    public void RetrievedDocumentRejectsEmptyRequiredText(string emptyParameter)
    {
        var values = new Dictionary<string, string>
        {
            ["documentId"] = "id",
            ["title"] = "title",
            ["unityVersion"] = "6000.3",
            ["content"] = "content",
            ["contentHash"] = "hash"
        };
        values[emptyParameter] = " ";

        Assert.Throws<ArgumentException>(() => new RetrievedDocument(
            values["documentId"], UnityUrl, values["title"], values["unityVersion"],
            values["content"], values["contentHash"], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ChunkStoresStableDocumentReferenceAndSection()
    {
        var chunk = new DocumentChunk("game-object:0", "game-object", "Description", "Description", 0, 3);

        Assert.Equal("game-object", chunk.DocumentId);
        Assert.Equal("Description", chunk.Section);
        Assert.Equal(0, chunk.Ordinal);
    }

    [Fact]
    public void ChunkRejectsNegativeOrdinal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunk("chunk", "document", "text", null, -1, 1));
    }
}
