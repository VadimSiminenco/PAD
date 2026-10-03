using AngleSharp.Html.Parser;
using System.Text.Json;

namespace UnityDocsRag.Infrastructure.Documentation;

public sealed class UnityDocumentationLinkExtractor
{
    private const string AllowedPathPrefix = "/6000.3/Documentation/ScriptReference/";
    private static readonly StringComparer UrlComparer = StringComparer.Ordinal;
    private static readonly Uri ScriptReferenceBase = new("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/");

    public Uri FindTocUrl(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var candidates = new SortedSet<string>(UrlComparer);

        foreach (var script in document.QuerySelectorAll("script[src]"))
        {
            var src = script.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(src) || !Uri.TryCreate(pageUrl, src.Trim(), out var resolved))
            {
                continue;
            }

            if (IsAllowedToc(resolved))
            {
                candidates.Add(resolved.AbsoluteUri);
            }
        }

        if (candidates.Count != 1)
        {
            throw new InvalidDataException(candidates.Count == 0
                ? "The Unity index does not contain an allowed docdata/toc.js script."
                : "The Unity index contains multiple different allowed docdata/toc.js scripts.");
        }

        return new Uri(candidates.Min!, UriKind.Absolute);
    }

    public IReadOnlyList<Uri> ExtractPageLinksFromToc(string javascript)
    {
        ArgumentNullException.ThrowIfNull(javascript);
        var source = javascript.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!source.StartsWith("var toc", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Unity TOC must begin with a 'var toc =' declaration.");
        }

        var position = "var toc".Length;
        while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
        if (position >= source.Length || source[position++] != '=')
        {
            throw new InvalidDataException("The Unity TOC must begin with a 'var toc =' declaration.");
        }

        var json = source[position..].Trim();
        if (json.EndsWith(';')) json = json[..^1].TrimEnd();
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Unity TOC contains malformed JSON.", exception);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Unity TOC JSON root must be an object.");
            var links = new SortedSet<string>(UrlComparer);
            Visit(parsed.RootElement, links);
            if (links.Count == 0)
                throw new InvalidDataException("The Unity TOC contains no allowed documentation pages.");
            return links.Select(link => new Uri(link, UriKind.Absolute)).ToArray();
        }
    }

    public IReadOnlyList<Uri> ExtractDirectMemberPageLinks(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<Uri>();

        try
        {
            var document = new HtmlParser().ParseDocument(html);
            var mainSection = document.QuerySelector(".section");
            if (mainSection is null) return Array.Empty<Uri>();

            var links = new SortedSet<string>(UrlComparer);
            foreach (var subsection in mainSection.Children.Where(element => element.ClassList.Contains("subsection")))
            {
                if (IsInheritedMembersSection(subsection) || !IsDirectMemberSection(subsection)) continue;

                foreach (var table in subsection.QuerySelectorAll("table.list"))
                {
                    if (HasNestedSubsection(table, subsection)) continue;
                    foreach (var anchor in table.QuerySelectorAll("a[href]"))
                    {
                        var href = anchor.GetAttribute("href")?.Trim();
                        if (string.IsNullOrEmpty(href) || !Uri.TryCreate(pageUrl, href, out var resolved)) continue;
                        if (IsAllowedPage(resolved) && !Uri.Compare(
                                resolved, pageUrl, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal).Equals(0))
                            links.Add(resolved.AbsoluteUri);
                    }
                }
            }

            return links.Select(link => new Uri(link, UriKind.Absolute)).ToArray();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Array.Empty<Uri>();
        }
    }

    private static bool IsInheritedMembersSection(AngleSharp.Dom.IElement subsection)
    {
        var heading = subsection.QuerySelector("h2, h3, h4, .subsection-title");
        var text = NormalizeWhitespace(heading?.TextContent);
        return text.StartsWith("Inherited Members", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectMemberSection(AngleSharp.Dom.IElement subsection)
    {
        var heading = subsection.QuerySelector("h2, h3, h4, .subsection-title");
        var text = NormalizeWhitespace(heading?.TextContent);
        return text.Contains("propert", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("constructor", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("operator", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("method", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNestedSubsection(AngleSharp.Dom.IElement element, AngleSharp.Dom.IElement outerSubsection)
    {
        for (var parent = element.ParentElement; parent is not null && !ReferenceEquals(parent, outerSubsection); parent = parent.ParentElement)
        {
            if (parent.ClassList.Contains("subsection")) return true;
        }

        return false;
    }

    private static void Visit(JsonElement element, SortedSet<string> links)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Visit(child, links);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("link", out var linkElement) && linkElement.ValueKind == JsonValueKind.String)
        {
            var link = linkElement.GetString()?.Trim();
            if (!string.IsNullOrEmpty(link) &&
                !string.Equals(link, "null", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(link, "toc", StringComparison.Ordinal))
            {
                if (!link.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) link += ".html";
                if (Uri.TryCreate(ScriptReferenceBase, link, out var resolved) && IsAllowedPage(resolved))
                    links.Add(resolved.AbsoluteUri);
            }
        }
        if (element.TryGetProperty("children", out var children)) Visit(children, links);
    }

    private static bool IsAllowedToc(Uri uri) => IsAllowedHost(uri) &&
        uri.AbsolutePath.StartsWith("/6000.3/Documentation/ScriptReference/docdata/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith("/toc.js", StringComparison.Ordinal);

    private static bool IsAllowedHost(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "docs.unity3d.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public string GetTitle(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);

        var document = new HtmlParser().ParseDocument(html);
        var heading = NormalizeWhitespace(document.QuerySelector("h1")?.TextContent);
        if (heading.Length > 0)
        {
            return heading;
        }

        var title = NormalizeWhitespace(document.Title);
        if (title.Length > 0)
        {
            return title;
        }

        var fileName = Path.GetFileNameWithoutExtension(pageUrl.AbsolutePath);
        var fallback = Uri.UnescapeDataString(fileName);
        return fallback.Length > 0 ? fallback : pageUrl.Host;
    }

    private static bool IsAllowedPage(Uri uri) => IsAllowedHost(uri) &&
        uri.AbsolutePath.StartsWith(AllowedPathPrefix, StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
