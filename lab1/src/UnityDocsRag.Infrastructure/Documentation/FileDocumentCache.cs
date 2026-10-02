using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Infrastructure.Documentation;

public enum DocumentCacheChangeKind
{
    New,
    Updated,
    Unchanged
}

public sealed record DocumentCacheResult(DocumentCacheChangeKind ChangeKind, string RelativeFileName);

public sealed record FileDocumentCacheManifest(int Version, IReadOnlyList<FileDocumentCacheEntry> Documents);

public sealed record FileDocumentCacheEntry(
    string DocumentId,
    string Url,
    string Title,
    string ContentHash,
    string FileName,
    DateTimeOffset RetrievedAt);

public sealed class FileDocumentCache
{
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rawDirectory;
    private readonly string _manifestPath;

    public FileDocumentCache(UnityDocumentationSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _rawDirectory = Path.GetFullPath(options.RawOutputDirectory);
        _manifestPath = Path.GetFullPath(options.ManifestPath);
    }

    public async Task<DocumentCacheResult> SaveAsync(RetrievedDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rawDirectory);
        var manifestDirectory = Path.GetDirectoryName(_manifestPath)
            ?? throw new InvalidOperationException("Manifest path must have a parent directory.");
        Directory.CreateDirectory(manifestDirectory);

        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var existingEntries = manifest.Documents.ToList();
        if (existingEntries.Count(entry => string.Equals(entry.DocumentId, document.DocumentId, StringComparison.Ordinal)) > 1)
        {
            throw new InvalidDataException($"Cache manifest contains duplicate document ID '{document.DocumentId}'.");
        }

        var previous = existingEntries.FirstOrDefault(entry =>
            string.Equals(entry.DocumentId, document.DocumentId, StringComparison.Ordinal));

        var fileName = previous?.FileName ?? CreateFileName(document.DocumentId);
        ValidateFileName(fileName);
        var targetPath = Path.Combine(_rawDirectory, fileName);
        var changeKind = previous is null
            ? DocumentCacheChangeKind.New
            : string.Equals(previous.ContentHash, document.ContentHash, StringComparison.OrdinalIgnoreCase)
                ? DocumentCacheChangeKind.Unchanged
                : DocumentCacheChangeKind.Updated;

        if (changeKind == DocumentCacheChangeKind.Unchanged && !File.Exists(targetPath))
        {
            throw new InvalidDataException(
                $"Cache manifest references '{fileName}', but the cached HTML file is missing.");
        }

        var updatedEntry = new FileDocumentCacheEntry(
            document.DocumentId,
            document.CanonicalUrl.AbsoluteUri,
            document.Title,
            document.ContentHash,
            fileName,
            document.RetrievedAt.ToUniversalTime());
        if (previous is null)
        {
            existingEntries.Add(updatedEntry);
        }
        else
        {
            existingEntries[existingEntries.IndexOf(previous)] = updatedEntry;
        }

        var updatedManifest = new FileDocumentCacheManifest(
            ManifestVersion,
            existingEntries.OrderBy(entry => entry.DocumentId, StringComparer.Ordinal).ToArray());
        var manifestJson = JsonSerializer.Serialize(updatedManifest, JsonOptions);

        var manifestTempPath = CreateTemporaryPath(manifestDirectory, "manifest");
        string? htmlTempPath = null;
        string? backupPath = null;
        var htmlReplaced = false;
        var manifestReplaced = false;
        try
        {
            await File.WriteAllTextAsync(manifestTempPath, manifestJson, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            if (changeKind != DocumentCacheChangeKind.Unchanged)
            {
                htmlTempPath = CreateTemporaryPath(_rawDirectory, "document");
                await File.WriteAllTextAsync(htmlTempPath, document.Content, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                if (File.Exists(targetPath))
                {
                    backupPath = CreateTemporaryPath(_rawDirectory, "backup");
                    File.Copy(targetPath, backupPath, overwrite: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (htmlTempPath is not null)
            {
                File.Move(htmlTempPath, targetPath, overwrite: true);
                htmlTempPath = null;
                htmlReplaced = true;
            }

            File.Move(manifestTempPath, _manifestPath, overwrite: true);
            manifestTempPath = string.Empty;
            manifestReplaced = true;

            return new DocumentCacheResult(changeKind, fileName);
        }
        catch
        {
            if (htmlReplaced && !manifestReplaced)
            {
                if (backupPath is not null && File.Exists(backupPath))
                {
                    File.Move(backupPath, targetPath, overwrite: true);
                    backupPath = null;
                }
                else if (previous is null && File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
            }

            throw;
        }
        finally
        {
            DeleteIfPresent(manifestTempPath);
            if (htmlTempPath is not null)
            {
                DeleteIfPresent(htmlTempPath);
            }

            if (backupPath is not null)
            {
                DeleteIfPresent(backupPath);
            }
        }
    }

    private async Task<FileDocumentCacheManifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath))
        {
            return new FileDocumentCacheManifest(ManifestVersion, Array.Empty<FileDocumentCacheEntry>());
        }

        FileDocumentCacheManifest? manifest;
        try
        {
            var json = await File.ReadAllTextAsync(_manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<FileDocumentCacheManifest>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Cache manifest '{_manifestPath}' contains invalid JSON.", exception);
        }

        if (manifest is null || manifest.Version != ManifestVersion || manifest.Documents is null)
        {
            throw new InvalidDataException(
                $"Cache manifest '{_manifestPath}' is empty, has an unsupported version, or has no documents list.");
        }

        foreach (var entry in manifest.Documents)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.DocumentId) ||
                string.IsNullOrWhiteSpace(entry.Url) ||
                string.IsNullOrWhiteSpace(entry.Title) ||
                string.IsNullOrWhiteSpace(entry.ContentHash) ||
                string.IsNullOrWhiteSpace(entry.FileName) ||
                !Uri.TryCreate(entry.Url, UriKind.Absolute, out _) ||
                entry.RetrievedAt == default)
            {
                throw new InvalidDataException($"Cache manifest '{_manifestPath}' contains an invalid document entry.");
            }

            ValidateFileName(entry.FileName);
        }

        if (manifest.Documents.Select(entry => entry.DocumentId).Distinct(StringComparer.Ordinal).Count() != manifest.Documents.Count)
        {
            throw new InvalidDataException($"Cache manifest '{_manifestPath}' contains duplicate document IDs.");
        }

        if (manifest.Documents.Select(entry => entry.FileName).Distinct(StringComparer.Ordinal).Count() != manifest.Documents.Count)
        {
            throw new InvalidDataException($"Cache manifest '{_manifestPath}' contains duplicate HTML file names.");
        }

        return manifest;
    }

    private static string CreateFileName(string documentId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId))).ToLowerInvariant();
        return $"{hash}.html";
    }

    private static void ValidateFileName(string fileName)
    {
        if (Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName is "." or "..")
        {
            throw new InvalidDataException("Cache manifest contains an unsafe or non-relative HTML file name.");
        }
    }

    private static string CreateTemporaryPath(string directory, string purpose) =>
        Path.Combine(directory, $".{purpose}-{Guid.NewGuid():N}.tmp");

    private static void DeleteIfPresent(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
