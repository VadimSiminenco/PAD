namespace UnityDocsRag.Infrastructure.Embeddings;

public sealed class OllamaEmbeddingOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:11434/api/embed";
    public int BatchSize { get; init; } = 8;
    public bool Truncate { get; init; }
    public string KeepAlive { get; init; } = "5m";

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URI.", nameof(Endpoint));
        var host = endpoint.Host.Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Endpoint host must be loopback: 127.0.0.1, localhost, or ::1.", nameof(Endpoint));
        if (endpoint.AbsolutePath is not ("/api/embed" or "/api/embed/") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Endpoint path must be /api/embed (optionally with a trailing slash) without user info, query, or fragment.", nameof(Endpoint));
        if (BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(BatchSize), "Batch size must be positive.");
        if (string.IsNullOrWhiteSpace(KeepAlive)) throw new ArgumentException("KeepAlive must not be empty.", nameof(KeepAlive));
    }
}
