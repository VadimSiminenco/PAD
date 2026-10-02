using System.Text.Json;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Tests.Preprocessing;

public sealed class FileProcessingArtifactStoreTests
{
    [Fact]
    public async Task WritesDeterministicSelfContainedJsonWithoutTemporaryFilesOrLocalPaths()
    {
        var root = Temp();
        try
        {
            var options = Options(root);
            var docB = Document("doc-b", "Beta");
            var docA = Document("doc-a", "Alpha");
            var chunkB = Chunk("chunk-b", "doc-b", 0, "second");
            var chunkA = Chunk("chunk-a", "doc-a", 0, "first");
            await new FileProcessingArtifactStore(options).SaveAsync(new[] { docB, docA }, new[] { chunkB, chunkA }, CancellationToken.None);

            var documentsText = await File.ReadAllTextAsync(options.ProcessedDocumentsPath);
            var chunksText = await File.ReadAllTextAsync(options.ChunksPath);
            using var documentsJson = JsonDocument.Parse(documentsText);
            using var chunksJson = JsonDocument.Parse(chunksText);
            var documents = documentsJson.RootElement.GetProperty("Documents").EnumerateArray().ToArray();
            var chunks = chunksJson.RootElement.GetProperty("Chunks").EnumerateArray().ToArray();
            Assert.Equal(new[] { "doc-a", "doc-b" }, documents.Select(item => item.GetProperty("DocumentId").GetString()));
            Assert.Equal(new[] { "doc-a", "doc-b" }, chunks.Select(item => item.GetProperty("DocumentId").GetString()));
            var chunk = chunks[0];
            Assert.Equal("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/A.html", chunk.GetProperty("SourceUrl").GetString());
            Assert.Equal("Alpha", chunk.GetProperty("DocumentTitle").GetString());
            Assert.Equal("processed-hash-doc-a", chunk.GetProperty("ProcessedContentHash").GetString());
            Assert.True(chunk.TryGetProperty("ApproximateTokenCount", out _));
            Assert.DoesNotContain("GeneratedAt", documentsText, StringComparison.Ordinal);
            Assert.DoesNotContain("GeneratedAt", chunksText, StringComparison.Ordinal);
            Assert.DoesNotContain(root, documentsText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, chunksText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(options.ProcessedDocumentsPath)!, "*.tmp"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(options.ChunksPath)!, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsChunkWithUnknownDocumentIdBeforeWriting()
    {
        var root = Temp();
        try
        {
            var options = Options(root);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new FileProcessingArtifactStore(options).SaveAsync(new[] { Document("doc-a", "Alpha") },
                    new[] { Chunk("chunk-x", "missing", 0, "orphan") }, CancellationToken.None));
            Assert.Contains("unknown DocumentId", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(options.ProcessedDocumentsPath));
            Assert.False(File.Exists(options.ChunksPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ProcessingRunOptions Options(string root) => new()
    {
        RawInputDirectory = Path.Combine(root, "raw"), ManifestPath = Path.Combine(root, "manifest.json"),
        ProcessedDocumentsPath = Path.Combine(root, "processed", "documents.json"), ChunksPath = Path.Combine(root, "chunks", "chunks.json"),
        ChunkSize = 8, ChunkOverlap = 2
    };
    private static string Temp() => Path.Combine(Path.GetTempPath(), "unitydocs-artifacts-test-" + Guid.NewGuid().ToString("N"));
    private static ProcessedDocument Document(string id, string title) => new(id,
        new Uri($"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/{(id == "doc-a" ? "A" : "B")}.html"),
        title, "6000.3", "processed " + title, "processed-hash-" + id, DateTimeOffset.UnixEpoch,
        new Dictionary<string, string> { ["sourceContentHash"] = "source-hash" });
    private static DocumentChunk Chunk(string id, string documentId, int ordinal, string text) =>
        new(id, documentId, text, "Description", ordinal, text.Split(' ').Length);
}
