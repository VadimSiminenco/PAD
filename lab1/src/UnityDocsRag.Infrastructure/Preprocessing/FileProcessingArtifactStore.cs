using System.Text;
using System.Text.Json;
using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Infrastructure.Preprocessing;

public sealed class FileProcessingArtifactStore
{
    private readonly ProcessingRunOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public FileProcessingArtifactStore(ProcessingRunOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task SaveAsync(IReadOnlyList<ProcessedDocument> documents, IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        var orderedDocuments = documents.OrderBy(document => document.DocumentId, StringComparer.Ordinal).ToArray();
        var orderedChunks = chunks.OrderBy(chunk => chunk.DocumentId, StringComparer.Ordinal).ThenBy(chunk => chunk.Ordinal).ToArray();
        var documentIds = orderedDocuments.Select(document => document.DocumentId).ToHashSet(StringComparer.Ordinal);
        if (documentIds.Count != orderedDocuments.Length) throw new InvalidDataException("Processed documents contain duplicate DocumentId values.");
        if (orderedChunks.Select(chunk => chunk.ChunkId).Distinct(StringComparer.Ordinal).Count() != orderedChunks.Length)
            throw new InvalidDataException("Chunks contain duplicate ChunkId values.");
        var unknown = orderedChunks.FirstOrDefault(chunk => !documentIds.Contains(chunk.DocumentId));
        if (unknown is not null) throw new InvalidDataException($"Chunk '{unknown.ChunkId}' references unknown DocumentId '{unknown.DocumentId}'.");

        var documentArtifact = new DocumentsArtifact(1, _options.UnityVersion, _options.ChunkSize, _options.ChunkOverlap,
            orderedDocuments.Select(document => new ProcessedDocumentArtifact(document.DocumentId, document.CanonicalUrl.AbsoluteUri,
                document.Title, document.UnityVersion, document.Content, document.ContentHash, document.RetrievedAt, document.Metadata)).ToArray());
        var byId = orderedDocuments.ToDictionary(document => document.DocumentId, StringComparer.Ordinal);
        var chunkArtifact = new ChunksArtifact(1, _options.UnityVersion, _options.ChunkSize, _options.ChunkOverlap,
            orderedChunks.Select(chunk =>
            {
                var doc = byId[chunk.DocumentId];
                return new ChunkArtifact(chunk.ChunkId, chunk.DocumentId, doc.CanonicalUrl.AbsoluteUri, doc.Title,
                    doc.UnityVersion, doc.ContentHash, chunk.Section, chunk.Ordinal, chunk.ApproximateTokenCount, chunk.Text);
            }).ToArray());

        var documentJson = JsonSerializer.Serialize(documentArtifact, _jsonOptions);
        var chunksJson = JsonSerializer.Serialize(chunkArtifact, _jsonOptions);
        var documentPath = Path.GetFullPath(_options.ProcessedDocumentsPath);
        var chunksPath = Path.GetFullPath(_options.ChunksPath);
        var documentTemp = await WriteTemporaryAsync(documentPath, documentJson, cancellationToken).ConfigureAwait(false);
        string? chunksTemp = null;
        try
        {
            chunksTemp = await WriteTemporaryAsync(chunksPath, chunksJson, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(documentTemp, documentPath, overwrite: true);
            documentTemp = string.Empty;
            File.Move(chunksTemp, chunksPath, overwrite: true);
            chunksTemp = null;
        }
        finally
        {
            DeleteTemporary(documentTemp);
            if (chunksTemp is not null) DeleteTemporary(chunksTemp);
        }
    }

    private static async Task<string> WriteTemporaryAsync(string target, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Artifact path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            return temporary;
        }
        catch
        {
            DeleteTemporary(temporary);
            throw;
        }
    }

    private static void DeleteTemporary(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
    }

    public sealed record DocumentsArtifact(int Version, string UnityVersion, int ChunkSize, int ChunkOverlap,
        IReadOnlyList<ProcessedDocumentArtifact> Documents);
    public sealed record ProcessedDocumentArtifact(string DocumentId, string CanonicalUrl, string Title, string UnityVersion,
        string Content, string ContentHash, DateTimeOffset RetrievedAt, IReadOnlyDictionary<string, string> Metadata);
    public sealed record ChunksArtifact(int Version, string UnityVersion, int ChunkSize, int ChunkOverlap,
        IReadOnlyList<ChunkArtifact> Chunks);
    public sealed record ChunkArtifact(string ChunkId, string DocumentId, string SourceUrl, string DocumentTitle,
        string UnityVersion, string ProcessedContentHash, string? Section, int Ordinal, int ApproximateTokenCount, string Text);
}
