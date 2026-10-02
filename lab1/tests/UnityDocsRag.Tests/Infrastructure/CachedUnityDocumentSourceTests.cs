using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Infrastructure.Documentation;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Tests.Infrastructure;

public sealed class CachedUnityDocumentSourceTests
{
    [Fact]
    public async Task ReadsValidCacheInDocumentIdOrderWithExpectedFieldsAndMetadata()
    {
        var root = Temp();
        try
        {
            var options = Options(root);
            Directory.CreateDirectory(options.RawInputDirectory);
            await WriteEntryAsync(options, "doc-b", "B.html", "<h1>B</h1>");
            await WriteEntryAsync(options, "doc-a", "A.html", "<h1>A</h1>");
            await WriteManifestAsync(options, Entry("doc-b", "B.html", "<h1>B</h1>"), Entry("doc-a", "A.html", "<h1>A</h1>"));
            var documents = await CollectAsync(new CachedUnityDocumentSource(options).GetDocumentsAsync(CancellationToken.None));
            Assert.Equal(new[] { "doc-a", "doc-b" }, documents.Select(document => document.DocumentId));
            Assert.Equal("<h1>A</h1>", documents[0].Content);
            Assert.Equal("6000.3", documents[0].UnityVersion);
            Assert.Equal("local-cache", documents[0].Metadata["source"]);
            Assert.Equal("A.html", documents[0].Metadata["cacheFileName"]);
            Assert.Equal("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/A.html", documents[0].CanonicalUrl.AbsoluteUri);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HashMismatchIsRejected()
    {
        var root = Temp();
        try
        {
            var options = Options(root); Directory.CreateDirectory(options.RawInputDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.RawInputDirectory, "A.html"), "changed");
            await WriteManifestAsync(options, Entry("doc-a", "A.html", "original"));
            await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new CachedUnityDocumentSource(options).GetDocumentsAsync(CancellationToken.None)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingHtmlIsRejected()
    {
        var root = Temp();
        try
        {
            var options = Options(root); Directory.CreateDirectory(options.RawInputDirectory);
            await WriteManifestAsync(options, Entry("doc-a", "missing.html", "x"));
            await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new CachedUnityDocumentSource(options).GetDocumentsAsync(CancellationToken.None)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PathTraversalIsRejected()
    {
        var root = Temp();
        try
        {
            var options = Options(root); Directory.CreateDirectory(options.RawInputDirectory);
            await WriteManifestAsync(options, Entry("doc-a", "..\\outside.html", "x"));
            await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new CachedUnityDocumentSource(options).GetDocumentsAsync(CancellationToken.None)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WrongUnityUrlIsRejected()
    {
        var root = Temp();
        try
        {
            var options = Options(root); Directory.CreateDirectory(options.RawInputDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.RawInputDirectory, "A.html"), "x");
            var entry = Entry("doc-a", "A.html", "x") with { Url = "https://example.com/6000.3/Documentation/ScriptReference/A.html" };
            await WriteManifestAsync(options, entry);
            await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new CachedUnityDocumentSource(options).GetDocumentsAsync(CancellationToken.None)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BadManifestVersionAndEmptyDocumentsAreRejected()
    {
        var root = Temp();
        try
        {
            var options = Options(root); Directory.CreateDirectory(Path.GetDirectoryName(options.ManifestPath)!);
            await File.WriteAllTextAsync(options.ManifestPath, "{\"Version\":2,\"Documents\":[]}");
            await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new CachedUnityDocumentSource(options).GetDocumentsAsync(CancellationToken.None)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProcessingRunOptions Options(string root) => new()
    {
        RawInputDirectory = Path.Combine(root, "raw"), ManifestPath = Path.Combine(root, "state", "manifest.json"),
        ProcessedDocumentsPath = Path.Combine(root, "processed", "docs.json"), ChunksPath = Path.Combine(root, "chunks", "chunks.json")
    };
    private static string Temp() => Path.Combine(Path.GetTempPath(), "unitydocs-cache-test-" + Guid.NewGuid().ToString("N"));
    private static FileDocumentCacheEntry Entry(string id, string name, string content) => new(id,
        $"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/{Path.GetFileNameWithoutExtension(name)}.html", id,
        Hash(content), name, DateTimeOffset.UnixEpoch.AddDays(1));
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    private static async Task WriteEntryAsync(ProcessingRunOptions options, string id, string name, string html) =>
        await File.WriteAllTextAsync(Path.Combine(options.RawInputDirectory, name), html);
    private static async Task WriteManifestAsync(ProcessingRunOptions options, params FileDocumentCacheEntry[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.ManifestPath)!);
        await File.WriteAllTextAsync(options.ManifestPath, JsonSerializer.Serialize(new FileDocumentCacheManifest(1, entries)));
    }
    private static async Task<List<UnityDocsRag.Core.Documents.RetrievedDocument>> CollectAsync(IAsyncEnumerable<UnityDocsRag.Core.Documents.RetrievedDocument> source)
    {
        var result = new List<UnityDocsRag.Core.Documents.RetrievedDocument>();
        await foreach (var document in source) result.Add(document);
        return result;
    }
}
