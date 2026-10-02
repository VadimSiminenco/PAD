using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Tests.Preprocessing;

public sealed class ProcessingArtifactReaderTests
{
    [Fact]
    public async Task ReadsModelsSortsCollectionsAndPreservesMetadata()
    {
        await using var files = await Artifacts.CreateAsync();
        var result = await new ProcessingArtifactReader().ReadAsync(files.DocumentsPath, files.ChunksPath, CancellationToken.None);
        Assert.Equal(new[] { "doc-a", "doc-b" }, result.Documents.Select(item => item.DocumentId));
        Assert.Equal(new[] { "doc-a/0", "doc-a/1", "doc-b/0" }, result.Chunks.Select(item => $"{item.DocumentId}/{item.Ordinal}"));
        Assert.Equal("6000.3", result.UnityVersion);
        Assert.Equal(512, result.ChunkSize);
        Assert.Equal(64, result.ChunkOverlap);
        Assert.Equal("preserved", result.Documents[0].Metadata["custom"]);
        Assert.Throws<NotSupportedException>(() => ((IList<ProcessedDocument>)result.Documents)[0] = result.Documents[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<DocumentChunk>)result.Chunks).Clear());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsArtifactVersionOtherThanOne(bool alterChunks)
    {
        await using var files = await Artifacts.CreateAsync();
        if (alterChunks) files.Chunks = files.Chunks with { Version = 2 };
        else files.Documents = files.Documents with { Version = 2 };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Theory]
    [InlineData("unity")]
    [InlineData("size")]
    [InlineData("overlap")]
    public async Task RejectsMismatchedArtifactSettings(string setting)
    {
        await using var files = await Artifacts.CreateAsync();
        files.Chunks = setting switch
        {
            "unity" => files.Chunks with { UnityVersion = "6000.2" },
            "size" => files.Chunks with { ChunkSize = 256 },
            _ => files.Chunks with { ChunkOverlap = 32 }
        };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsIncorrectProcessedDocumentHash()
    {
        await using var files = await Artifacts.CreateAsync();
        files.Documents = files.Documents with { Documents = files.Documents.Documents.Select((doc, index) =>
            index == 0 ? doc with { ContentHash = new string('0', 64) } : doc).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsIncorrectChunkId()
    {
        await using var files = await Artifacts.CreateAsync();
        files.Chunks = files.Chunks with { Chunks = files.Chunks.Chunks.Select((chunk, index) =>
            index == 0 ? chunk with { ChunkId = new string('0', 64) } : chunk).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsIncorrectApproximateTokenCount()
    {
        await using var files = await Artifacts.CreateAsync();
        files.Chunks = files.Chunks with { Chunks = files.Chunks.Chunks.Select((chunk, index) =>
            index == 0 ? chunk with { ApproximateTokenCount = chunk.ApproximateTokenCount + 1 } : chunk).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsChunkWithUnknownDocument()
    {
        await using var files = await Artifacts.CreateAsync();
        var changed = files.Chunks.Chunks[0] with { DocumentId = "unknown" };
        files.Chunks = files.Chunks with { Chunks = files.Chunks.Chunks.Select((chunk, index) => index == 0 ? changed : chunk).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("title")]
    [InlineData("hash")]
    public async Task RejectsChunkSourceMetadataMismatch(string field)
    {
        await using var files = await Artifacts.CreateAsync();
        var chunks = files.Chunks.Chunks.ToArray();
        chunks[0] = field switch
        {
            "url" => chunks[0] with { SourceUrl = "https://example.invalid/other" },
            "title" => chunks[0] with { DocumentTitle = "other title" },
            _ => chunks[0] with { ProcessedContentHash = new string('0', 64) }
        };
        files.Chunks = files.Chunks with { Chunks = chunks };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsDuplicateDocumentIds()
    {
        await using var files = await Artifacts.CreateAsync();
        files.Documents = files.Documents with { Documents = files.Documents.Documents.Concat(new[] { files.Documents.Documents[0] }).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsDuplicateChunkIds()
    {
        await using var files = await Artifacts.CreateAsync();
        files.Chunks = files.Chunks with { Chunks = files.Chunks.Chunks.Concat(new[] { files.Chunks.Chunks[0] }).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsDuplicateDocumentOrdinals()
    {
        await using var files = await Artifacts.CreateAsync();
        var duplicate = MakeChunk(files.Documents.Documents[0], 0, "different text");
        files.Chunks = files.Chunks with { Chunks = files.Chunks.Chunks.Append(duplicate).ToArray() };
        await files.WriteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsMissingArtifactFile()
    {
        await using var files = await Artifacts.CreateAsync();
        File.Delete(files.ChunksPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
    }

    [Fact]
    public async Task RejectsMalformedJsonWithoutEchoingDocumentText()
    {
        await using var files = await Artifacts.CreateAsync();
        await File.WriteAllTextAsync(files.DocumentsPath, "{ \"content\": \"PRIVATE-CONTENT\"");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Read(files));
        Assert.DoesNotContain("PRIVATE-CONTENT", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        await using var files = await Artifacts.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessingArtifactReader()
            .ReadAsync(files.DocumentsPath, files.ChunksPath, cancellation.Token));
    }

    private static Task<ProcessingArtifactSnapshot> Read(Artifacts files) =>
        new ProcessingArtifactReader().ReadAsync(files.DocumentsPath, files.ChunksPath, CancellationToken.None);

    private static FileProcessingArtifactStore.ChunkArtifact MakeChunk(FileProcessingArtifactStore.ProcessedDocumentArtifact document,
        int ordinal, string text)
    {
        var id = Hash($"{document.DocumentId}\n{ordinal}\n{text}");
        return new(id, document.DocumentId, document.CanonicalUrl, document.Title, document.UnityVersion,
            document.ContentHash, "Description", ordinal, CountTokens(text), text);
    }

    private static int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class Artifacts : IAsyncDisposable
    {
        private Artifacts(string directory, FileProcessingArtifactStore.DocumentsArtifact documents,
            FileProcessingArtifactStore.ChunksArtifact chunks)
        {
            Directory = directory;
            Documents = documents;
            Chunks = chunks;
            DocumentsPath = Path.Combine(directory, "documents.json");
            ChunksPath = Path.Combine(directory, "chunks.json");
        }

        public string Directory { get; }
        public string DocumentsPath { get; }
        public string ChunksPath { get; }
        public FileProcessingArtifactStore.DocumentsArtifact Documents { get; set; }
        public FileProcessingArtifactStore.ChunksArtifact Chunks { get; set; }

        public static async Task<Artifacts> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"unitydocs-reader-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var documents = new[] { MakeDocument("doc-b"), MakeDocument("doc-a") };
            var chunks = new[]
            {
                MakeChunk(documents[0], 0, "b text"),
                MakeChunk(documents[1], 1, "a second text"),
                MakeChunk(documents[1], 0, "a first text")
            };
            var files = new Artifacts(directory,
                new(1, "6000.3", 512, 64, documents),
                new(1, "6000.3", 512, 64, chunks));
            await files.WriteAsync();
            return files;
        }

        public async Task WriteAsync()
        {
            await File.WriteAllTextAsync(DocumentsPath, JsonSerializer.Serialize(Documents));
            await File.WriteAllTextAsync(ChunksPath, JsonSerializer.Serialize(Chunks));
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static FileProcessingArtifactStore.ProcessedDocumentArtifact MakeDocument(string id)
        {
            const string content = "# API\n\n## Description\n\nexample body";
            return new(id, $"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/{id}.html", id,
                "6000.3", content, Hash(content), DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                new Dictionary<string, string> { ["custom"] = "preserved", ["id"] = id });
        }
    }
}
