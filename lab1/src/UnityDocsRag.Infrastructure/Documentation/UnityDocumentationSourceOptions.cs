namespace UnityDocsRag.Infrastructure.Documentation;

public sealed class UnityDocumentationSourceOptions
{
    public const string DefaultBaseUrl = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string UnityVersion { get; init; } = "6000.3";
    public int MaxPages { get; init; } = 5;
    public int RequestDelayMilliseconds { get; init; } = 250;
    public int RequestTimeoutSeconds { get; init; } = 30;
    public string UserAgent { get; init; } = "UnityDocsRag-Lab1/1.0 (educational project)";
    public string RawOutputDirectory { get; init; } = "data/raw/unity-6000.3";
    public string ManifestPath { get; init; } = "data/state/unity-6000.3-manifest.json";

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "docs.unity3d.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !uri.AbsolutePath.StartsWith("/6000.3/Documentation/ScriptReference/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "BaseUrl must be an absolute HTTPS URL inside the Unity 6000.3 Scripting API.",
                nameof(BaseUrl));
        }

        if (MaxPages is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPages), "MaxPages must be between 1 and 50.");
        }

        if (RequestDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestDelayMilliseconds), "Request delay cannot be negative.");
        }

        if (RequestTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds), "Request timeout must be positive.");
        }

        RequireNonEmpty(UserAgent, nameof(UserAgent));
        RequireNonEmpty(RawOutputDirectory, nameof(RawOutputDirectory));
        RequireNonEmpty(ManifestPath, nameof(ManifestPath));
    }

    private static void RequireNonEmpty(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty or whitespace.", parameterName);
        }
    }
}
