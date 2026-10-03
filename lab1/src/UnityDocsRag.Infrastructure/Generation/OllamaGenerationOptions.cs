namespace UnityDocsRag.Infrastructure.Generation;

public sealed class OllamaGenerationOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";
    public string Model { get; init; } = "qwen3:4b";
    public string KeepAlive { get; init; } = "5m";
    public int HttpTimeoutSeconds { get; init; } = 90;
    public double Temperature { get; init; }
    public int NumPredict { get; init; } = 512;
    public int NumCtx { get; init; } = 4096;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            (!string.Equals(endpoint.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Host.Trim('[', ']'), "::1", StringComparison.OrdinalIgnoreCase)) ||
            endpoint.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Endpoint must be a loopback HTTP or HTTPS origin.", nameof(Endpoint));
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Model must not be empty.", nameof(Model));
        if (string.IsNullOrWhiteSpace(KeepAlive)) throw new ArgumentException("KeepAlive must not be empty.", nameof(KeepAlive));
        if (HttpTimeoutSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(HttpTimeoutSeconds), "HTTP timeout must be between 1 and 3600 seconds.");
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(Temperature), "Temperature must be finite and between 0 and 2.");
        if (NumCtx is < 256 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(NumCtx), "num_ctx must be between 256 and 32768.");
        if (NumPredict is < 1 or > 2048 || NumPredict > NumCtx)
            throw new ArgumentOutOfRangeException(nameof(NumPredict), "num_predict must be between 1 and min(2048, num_ctx).");
    }

    public Uri CreateChatUri() => new(new Uri(Endpoint.TrimEnd('/') + "/", UriKind.Absolute), "api/chat");
}
