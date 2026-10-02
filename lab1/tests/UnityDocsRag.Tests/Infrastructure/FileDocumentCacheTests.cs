using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Documentation;

namespace UnityDocsRag.Tests.Infrastructure;

public sealed class FileDocumentCacheTests
{
    [Fact]
    public async Task FirstDocumentIsNewAndCreatesHtmlAndManifestWithoutAbsolutePaths()
    {
        var root = CreateUniqueDirectory();
        try
        {
            var options = CreateOptions(root);
            var cache = new FileDocumentCache(options);
            var result = await cache.SaveAsync(CreateDocument("page-1", "<h1>First</h1>"), CancellationToken.None);

            Assert.Equal(DocumentCacheChangeKind.New, result.ChangeKind);
            var htmlPath = Path.Combine(options.RawOutputDirectory, result.RelativeFileName);
            Assert.Equal("<h1>First</h1>", await File.ReadAllTextAsync(htmlPath));
            Assert.True(File.Exists(options.ManifestPath));
            var manifest = await File.ReadAllTextAsync(options.ManifestPath);
            Assert.DoesNotContain(root, manifest, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(manifest);
            Assert.Equal("page-1", json.RootElement.GetProperty("Documents")[0].GetProperty("DocumentId").GetString());
        }
        finally
        {
            DeleteUniqueDirectory(root);
        }
    }

    [Fact]
    public async Task SameHashIsUnchangedAndDoesNotCreateDuplicateHtml()
    {
        var root = CreateUniqueDirectory();
        try
        {
            var options = CreateOptions(root);
            var cache = new FileDocumentCache(options);
            var document = CreateDocument("page-1", "<h1>Stable</h1>");
            var first = await cache.SaveAsync(document, CancellationToken.None);
            var firstBytes = await File.ReadAllBytesAsync(Path.Combine(options.RawOutputDirectory, first.RelativeFileName));

            var result = await cache.SaveAsync(CreateDocument("page-1", "<h1>Stable</h1>"), CancellationToken.None);

            Assert.Equal(DocumentCacheChangeKind.Unchanged, result.ChangeKind);
            var htmlFiles = Directory.GetFiles(options.RawOutputDirectory, "*.html");
            Assert.Single(htmlFiles);
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(htmlFiles[0]));
        }
        finally
        {
            DeleteUniqueDirectory(root);
        }
    }

    [Fact]
    public async Task ChangedHashUpdatesHtmlAndManifestEntry()
    {
        var root = CreateUniqueDirectory();
        try
        {
            var options = CreateOptions(root);
            var cache = new FileDocumentCache(options);
            await cache.SaveAsync(CreateDocument("page-1", "old"), CancellationToken.None);

            var result = await cache.SaveAsync(CreateDocument("page-1", "new"), CancellationToken.None);

            Assert.Equal(DocumentCacheChangeKind.Updated, result.ChangeKind);
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(options.RawOutputDirectory, result.RelativeFileName)));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(options.ManifestPath));
            Assert.Equal(ContentHash("new"), manifest.RootElement.GetProperty("Documents")[0].GetProperty("ContentHash").GetString());
            Assert.Single(Directory.GetFiles(options.RawOutputDirectory, "*.html"));
        }
        finally
        {
            DeleteUniqueDirectory(root);
        }
    }

    [Fact]
    public async Task CorruptManifestRaisesClearDataError()
    {
        var root = CreateUniqueDirectory();
        try
        {
            var options = CreateOptions(root);
            Directory.CreateDirectory(Path.GetDirectoryName(options.ManifestPath)!);
            await File.WriteAllTextAsync(options.ManifestPath, "{ definitely not valid JSON");
            var cache = new FileDocumentCache(options);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => cache.SaveAsync(CreateDocument("page-1", "content"), CancellationToken.None));

            Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteUniqueDirectory(root);
        }
    }

    private static string CreateUniqueDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"UnityDocsRagTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteUniqueDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static UnityDocumentationSourceOptions CreateOptions(string root) => new()
    {
        RawOutputDirectory = Path.Combine(root, "raw"),
        ManifestPath = Path.Combine(root, "state", "manifest.json")
    };

    private static RetrievedDocument CreateDocument(string id, string content) => new(
        id,
        new Uri($"https://docs.unity3d.com/6000.3/Documentation/ScriptReference/{id}.html"),
        id,
        "6000.3",
        content,
        ContentHash(content),
        DateTimeOffset.UtcNow,
        new Dictionary<string, string> { ["source"] = "test" });

    private static string ContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
