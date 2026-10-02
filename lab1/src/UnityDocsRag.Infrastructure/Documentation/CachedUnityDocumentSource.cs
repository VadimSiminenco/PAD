using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Infrastructure.Documentation;

public sealed class CachedUnityDocumentSource : IDocumentSource
{
    private readonly ProcessingRunOptions _options;

    public CachedUnityDocumentSource(ProcessingRunOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async IAsyncEnumerable<RetrievedDocument> GetDocumentsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifestPath = Path.GetFullPath(_options.ManifestPath);
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"Local cache manifest '{manifestPath}' does not exist.");
        FileDocumentCacheManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<FileDocumentCacheManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Local cache manifest '{manifestPath}' is malformed JSON.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"Could not read local cache manifest '{manifestPath}'.", exception);
        }

        if (manifest is null || manifest.Version != 1 || manifest.Documents is null || manifest.Documents.Count == 0)
            throw new InvalidDataException("Local cache manifest must have Version=1 and a non-empty Documents list.");
        ValidateUniqueEntries(manifest.Documents);
        var rawRoot = Path.GetFullPath(_options.RawInputDirectory);
        foreach (var entry in manifest.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(entry);
            var url = ParseAllowedUrl(entry.Url);
            var filePath = Path.GetFullPath(Path.Combine(rawRoot, entry.FileName));
            var relativePath = Path.GetRelativePath(rawRoot, filePath);
            if (Path.IsPathRooted(relativePath) || relativePath is "." or ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Cache file '{entry.FileName}' resolves outside the raw input directory.");
            if (!File.Exists(filePath)) throw new InvalidDataException($"Cached HTML file '{entry.FileName}' is missing.");
            string html;
            try { html = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false); }
            catch (IOException exception) { throw new InvalidDataException($"Could not read cached HTML file '{entry.FileName}'.", exception); }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
            if (!string.Equals(hash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Cached HTML file '{entry.FileName}' does not match its manifest ContentHash.");
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "local-cache",
                ["cacheFileName"] = entry.FileName
            };
            yield return new RetrievedDocument(entry.DocumentId, url, entry.Title, _options.UnityVersion, html, hash,
                entry.RetrievedAt, metadata);
        }
    }

    private void ValidateUniqueEntries(IReadOnlyList<FileDocumentCacheEntry> entries)
    {
        if (entries.Any(entry => entry is null)) throw new InvalidDataException("Local cache manifest contains a null document entry.");
        if (entries.Select(entry => entry.DocumentId).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidDataException("Local cache manifest contains duplicate DocumentId values.");
        if (entries.Select(entry => entry.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("Local cache manifest contains duplicate FileName values.");
    }

    private void ValidateEntry(FileDocumentCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DocumentId) || string.IsNullOrWhiteSpace(entry.Url) ||
            string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.ContentHash) ||
            string.IsNullOrWhiteSpace(entry.FileName) || entry.RetrievedAt == default)
            throw new InvalidDataException("Local cache manifest contains an incomplete document entry.");
        var name = entry.FileName;
        if (Path.IsPathRooted(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new InvalidDataException($"Cache manifest contains unsafe file name '{name}'.");
    }

    private Uri ParseAllowedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !url.IsDefaultPort ||
            !string.Equals(url.Host, "docs.unity3d.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
            !url.AbsolutePath.StartsWith($"/{_options.UnityVersion}/Documentation/ScriptReference/", StringComparison.Ordinal))
            throw new InvalidDataException($"Cache manifest contains a URL outside the Unity {_options.UnityVersion} Scripting API: '{value}'.");
        return url;
    }
}
