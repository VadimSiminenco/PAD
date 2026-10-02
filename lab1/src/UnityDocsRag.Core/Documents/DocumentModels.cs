using System.Collections.ObjectModel;

namespace UnityDocsRag.Core.Documents;

/// <summary>A document as received from the configured source, before cleaning or normalization.</summary>
public sealed record RetrievedDocument
{
    public RetrievedDocument(
        string documentId,
        Uri canonicalUrl,
        string title,
        string unityVersion,
        string content,
        string contentHash,
        DateTimeOffset retrievedAt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        DocumentId = RequireText(documentId, nameof(documentId));
        CanonicalUrl = RequireAbsoluteUrl(canonicalUrl, nameof(canonicalUrl));
        Title = RequireText(title, nameof(title));
        UnityVersion = RequireText(unityVersion, nameof(unityVersion));
        Content = RequireText(content, nameof(content));
        ContentHash = RequireText(contentHash, nameof(contentHash));
        RetrievedAt = retrievedAt;
        Metadata = CopyMetadata(metadata);
    }

    public string DocumentId { get; }
    public Uri CanonicalUrl { get; }
    public string Title { get; }
    public string UnityVersion { get; }
    public string Content { get; }
    public string ContentHash { get; }
    public DateTimeOffset RetrievedAt { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty or whitespace.", parameterName)
            : value;

    internal static Uri RequireAbsoluteUrl(Uri value, string parameterName) =>
        value is null || !value.IsAbsoluteUri
            ? throw new ArgumentException("URL must be absolute.", parameterName)
            : value;

    internal static IReadOnlyDictionary<string, string> CopyMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        var copy = metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(metadata);
        return new ReadOnlyDictionary<string, string>(copy);
    }
}

/// <summary>A retrieved document after cleaning, normalization, and metadata enrichment.</summary>
public sealed record ProcessedDocument
{
    public ProcessedDocument(
        string documentId,
        Uri canonicalUrl,
        string title,
        string unityVersion,
        string content,
        string contentHash,
        DateTimeOffset retrievedAt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        DocumentId = RetrievedDocument.RequireText(documentId, nameof(documentId));
        CanonicalUrl = RetrievedDocument.RequireAbsoluteUrl(canonicalUrl, nameof(canonicalUrl));
        Title = RetrievedDocument.RequireText(title, nameof(title));
        UnityVersion = RetrievedDocument.RequireText(unityVersion, nameof(unityVersion));
        Content = RetrievedDocument.RequireText(content, nameof(content));
        ContentHash = RetrievedDocument.RequireText(contentHash, nameof(contentHash));
        RetrievedAt = retrievedAt;
        Metadata = RetrievedDocument.CopyMetadata(metadata);
    }

    public string DocumentId { get; }
    public Uri CanonicalUrl { get; }
    public string Title { get; }
    public string UnityVersion { get; }
    public string Content { get; }
    public string ContentHash { get; }
    public DateTimeOffset RetrievedAt { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>A stable, ordered text segment derived from a processed document.</summary>
public sealed record DocumentChunk
{
    public DocumentChunk(
        string chunkId,
        string documentId,
        string text,
        string? section,
        int ordinal,
        int approximateTokenCount)
    {
        ChunkId = RetrievedDocument.RequireText(chunkId, nameof(chunkId));
        DocumentId = RetrievedDocument.RequireText(documentId, nameof(documentId));
        Text = RetrievedDocument.RequireText(text, nameof(text));
        Section = string.IsNullOrWhiteSpace(section) ? null : section;
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), "Chunk ordinal cannot be negative.");
        }

        if (approximateTokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approximateTokenCount), "Token count cannot be negative.");
        }

        Ordinal = ordinal;
        ApproximateTokenCount = approximateTokenCount;
    }

    public string ChunkId { get; }
    public string DocumentId { get; }
    public string Text { get; }
    public string? Section { get; }
    public int Ordinal { get; }
    public int ApproximateTokenCount { get; }
}
