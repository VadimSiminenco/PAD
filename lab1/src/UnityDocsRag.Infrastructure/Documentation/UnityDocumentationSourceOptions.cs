namespace UnityDocsRag.Infrastructure.Documentation;

public sealed class UnityDocumentationSourceOptions
{
    public const string DefaultBaseUrl = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string UnityVersion { get; init; } = "6000.3";
    public int MaxPages { get; init; } = 5;
    public IReadOnlyList<string> SeedPages { get; init; } = Array.Empty<string>();
    public bool IncludeMemberPages { get; init; }
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

        if (MaxPages is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPages), "MaxPages must be between 1 and 500.");
        }

        ArgumentNullException.ThrowIfNull(SeedPages);
        var seeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in SeedPages)
        {
            if (string.IsNullOrWhiteSpace(seed) ||
                Path.IsPathRooted(seed) ||
                seed.Contains('/') || seed.Contains('\\') ||
                seed.Contains('?') || seed.Contains('#') ||
                seed.Contains("..", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(seed), seed, StringComparison.Ordinal) ||
                !seed.EndsWith(".html", StringComparison.Ordinal))
                throw new ArgumentException("SeedPages entries must be relative .html file names without path, query, or fragment components.", nameof(SeedPages));
            if (!seeds.Add(seed))
                throw new ArgumentException("SeedPages entries must be unique using ordinal comparison.", nameof(SeedPages));
        }

        if (SeedPages.Count > MaxPages)
            throw new ArgumentException("MaxPages must be at least the number of configured seed pages.", nameof(MaxPages));

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
