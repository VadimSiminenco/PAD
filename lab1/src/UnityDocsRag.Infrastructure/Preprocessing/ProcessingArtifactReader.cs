using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Infrastructure.Preprocessing;

public sealed class ProcessingArtifactSnapshot
{
    public ProcessingArtifactSnapshot(IReadOnlyList<ProcessedDocument> documents, IReadOnlyList<DocumentChunk> chunks,
        string unityVersion, int chunkSize, int chunkOverlap)
    {
        Documents = Array.AsReadOnly(documents.ToArray());
        Chunks = Array.AsReadOnly(chunks.ToArray());
        UnityVersion = unityVersion;
        ChunkSize = chunkSize;
        ChunkOverlap = chunkOverlap;
    }

    public IReadOnlyList<ProcessedDocument> Documents { get; }
    public IReadOnlyList<DocumentChunk> Chunks { get; }
    public string UnityVersion { get; }
    public int ChunkSize { get; }
    public int ChunkOverlap { get; }
}

public sealed class ProcessingArtifactReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProcessingArtifactSnapshot> ReadAsync(string documentsPath, string chunksPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentsPath)) throw new InvalidDataException("Documents artifact path is empty.");
        if (string.IsNullOrWhiteSpace(chunksPath)) throw new InvalidDataException("Chunks artifact path is empty.");
        cancellationToken.ThrowIfCancellationRequested();
        var documentArtifact = await ReadArtifactAsync<FileProcessingArtifactStore.DocumentsArtifact>(documentsPath, "documents", cancellationToken).ConfigureAwait(false);
        var chunkArtifact = await ReadArtifactAsync<FileProcessingArtifactStore.ChunksArtifact>(chunksPath, "chunks", cancellationToken).ConfigureAwait(false);
        ValidateHeaders(documentArtifact.Version, documentArtifact.UnityVersion, documentArtifact.ChunkSize, documentArtifact.ChunkOverlap,
            chunkArtifact.Version, chunkArtifact.UnityVersion, chunkArtifact.ChunkSize, chunkArtifact.ChunkOverlap);
        if (documentArtifact.Documents is null || documentArtifact.Documents.Count == 0)
            throw new InvalidDataException("Documents artifact must contain at least one document.");
        if (chunkArtifact.Chunks is null || chunkArtifact.Chunks.Count == 0)
            throw new InvalidDataException("Chunks artifact must contain at least one chunk.");

        var documents = new List<ProcessedDocument>(documentArtifact.Documents.Count);
        var documentsById = new Dictionary<string, ProcessedDocument>(StringComparer.Ordinal);
        foreach (var artifact in documentArtifact.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.DocumentId) || string.IsNullOrWhiteSpace(artifact.Title) ||
                string.IsNullOrWhiteSpace(artifact.UnityVersion) || string.IsNullOrWhiteSpace(artifact.Content) ||
                string.IsNullOrWhiteSpace(artifact.ContentHash) || string.IsNullOrWhiteSpace(artifact.CanonicalUrl))
                throw new InvalidDataException("Documents artifact contains a document with missing required fields.");
            if (!string.Equals(artifact.UnityVersion, documentArtifact.UnityVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Document UnityVersion does not match artifact settings.");
            if (!string.Equals(Hash(artifact.Content), artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Processed document content hash is invalid.");
            if (!Uri.TryCreate(artifact.CanonicalUrl, UriKind.Absolute, out var uri))
                throw new InvalidDataException("Processed document canonical URL is invalid.");
            ProcessedDocument document;
            try
            {
                document = new ProcessedDocument(artifact.DocumentId, uri, artifact.Title, artifact.UnityVersion,
                    artifact.Content, artifact.ContentHash, artifact.RetrievedAt, artifact.Metadata);
            }
            catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
            {
                throw new InvalidDataException("Documents artifact contains an invalid document.");
            }
            if (!documentsById.TryAdd(document.DocumentId, document))
                throw new InvalidDataException("Documents artifact contains duplicate DocumentId values.");
            documents.Add(document);
        }

        var chunks = new List<DocumentChunk>(chunkArtifact.Chunks.Count);
        var chunkIds = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new HashSet<(string DocumentId, int Ordinal)>();
        var documentIdsWithChunks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in chunkArtifact.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.ChunkId) || string.IsNullOrWhiteSpace(artifact.DocumentId) ||
                string.IsNullOrWhiteSpace(artifact.SourceUrl) || string.IsNullOrWhiteSpace(artifact.DocumentTitle) ||
                string.IsNullOrWhiteSpace(artifact.UnityVersion) || string.IsNullOrWhiteSpace(artifact.ProcessedContentHash) ||
                string.IsNullOrWhiteSpace(artifact.Text) || artifact.Ordinal < 0 || artifact.ApproximateTokenCount < 0)
                throw new InvalidDataException("Chunks artifact contains a chunk with invalid required fields.");
            if (!documentsById.TryGetValue(artifact.DocumentId, out var document))
                throw new InvalidDataException("Chunk references a document that is not present in the documents artifact.");
            if (!string.Equals(artifact.SourceUrl, document.CanonicalUrl.AbsoluteUri, StringComparison.Ordinal) ||
                !string.Equals(artifact.DocumentTitle, document.Title, StringComparison.Ordinal) ||
                !string.Equals(artifact.UnityVersion, document.UnityVersion, StringComparison.Ordinal) ||
                !string.Equals(artifact.ProcessedContentHash, document.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chunk source metadata does not match its processed document.");
            if (!string.Equals(HashChunk(artifact.DocumentId, artifact.Ordinal, artifact.Text), artifact.ChunkId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ChunkId does not match the chunk content and position.");
            if (CountTokens(artifact.Text) != artifact.ApproximateTokenCount)
                throw new InvalidDataException("Chunk ApproximateTokenCount does not match its text.");
            if (!chunkIds.Add(artifact.ChunkId)) throw new InvalidDataException("Chunks artifact contains duplicate ChunkId values.");
            if (!ordinals.Add((artifact.DocumentId, artifact.Ordinal)))
                throw new InvalidDataException("Chunks artifact contains duplicate (DocumentId, Ordinal) pairs.");
            try
            {
                chunks.Add(new DocumentChunk(artifact.ChunkId, artifact.DocumentId, artifact.Text, artifact.Section,
                    artifact.Ordinal, artifact.ApproximateTokenCount));
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("Chunks artifact contains an invalid chunk.");
            }
            documentIdsWithChunks.Add(artifact.DocumentId);
        }
        if (documents.Any(document => !documentIdsWithChunks.Contains(document.DocumentId)))
            throw new InvalidDataException("Every processed document must have at least one chunk.");

        cancellationToken.ThrowIfCancellationRequested();
        return new ProcessingArtifactSnapshot(
            documents.OrderBy(document => document.DocumentId, StringComparer.Ordinal).ToArray(),
            chunks.OrderBy(chunk => chunk.DocumentId, StringComparer.Ordinal).ThenBy(chunk => chunk.Ordinal).ToArray(),
            documentArtifact.UnityVersion, documentArtifact.ChunkSize, documentArtifact.ChunkOverlap);
    }

    private static async Task<T> ReadArtifactAsync<T>(string path, string artifactName, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"The {artifactName} artifact file does not exist.");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException($"The {artifactName} artifact is empty or invalid.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The {artifactName} artifact contains malformed JSON.");
        }
        catch (IOException)
        {
            throw new InvalidDataException($"The {artifactName} artifact could not be read.");
        }
    }

    private static void ValidateHeaders(int docsVersion, string docsUnityVersion, int docsChunkSize, int docsChunkOverlap,
        int chunksVersion, string chunksUnityVersion, int chunksChunkSize, int chunksChunkOverlap)
    {
        if (docsVersion != 1 || chunksVersion != 1) throw new InvalidDataException("Processing artifact version must be 1.");
        if (string.IsNullOrWhiteSpace(docsUnityVersion) || docsChunkSize <= 0 || docsChunkOverlap < 0 || docsChunkOverlap >= docsChunkSize)
            throw new InvalidDataException("Documents artifact processing settings are invalid.");
        if (!string.Equals(docsUnityVersion, chunksUnityVersion, StringComparison.Ordinal) ||
            docsChunkSize != chunksChunkSize || docsChunkOverlap != chunksChunkOverlap)
            throw new InvalidDataException("Documents and chunks artifact settings do not match.");
    }

    private static int CountTokens(string text) => Regex.Matches(text, @"\S+").Count;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string HashChunk(string documentId, int ordinal, string text) =>
        Hash($"{documentId}\n{ordinal}\n{text}");
}
