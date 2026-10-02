using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Infrastructure.Preprocessing;

public sealed class UnityHtmlDocumentPreprocessor : IDocumentPreprocessor
{
    public Task<ProcessedDocument> ProcessAsync(RetrievedDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var dom = new HtmlParser().ParseDocument(document.Content);
        var main = dom.QuerySelector("#content-wrap .section");
        if (main is null) throw new InvalidDataException("Unity documentation main content '#content-wrap .section' was not found.");

        foreach (var node in main.QuerySelectorAll(".suggest, .suggest-success, .suggest-failed, .suggest-form, .scrollToFeedback, form, input, textarea, button, script, style, nav, header, footer, aside, img"))
            node.Remove();

        var title = Normalize(dom.QuerySelector("#content-wrap h1")?.TextContent) is { Length: > 0 } h1 ? h1 : document.Title;
        var output = new List<string> { "# " + title };
        RenderChildren(main, output, cancellationToken, subsectionDepth: 0, inServiceHeader: false);

        var content = TextNormalization.Normalize(string.Join("\n\n", output));
        if (content.Length == 0 || content == "# " + title)
            throw new InvalidDataException("Unity documentation main content contains no useful text.");
        var metadata = new Dictionary<string, string>(document.Metadata, StringComparer.Ordinal)
        {
            ["preprocessorVersion"] = "1",
            ["sourceContentHash"] = document.ContentHash
        };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return Task.FromResult(new ProcessedDocument(document.DocumentId, document.CanonicalUrl, document.Title,
            document.UnityVersion, content, hash, document.RetrievedAt, metadata));
    }

    private static void RenderChildren(IElement parent, List<string> output, CancellationToken cancellationToken,
        int subsectionDepth, bool inServiceHeader)
    {
        var serviceHeader = inServiceHeader || parent.ClassList.Contains("mb20");
        foreach (var child in parent.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = child.TagName.ToLowerInvariant();
            var childDepth = subsectionDepth + (child.ClassList.Contains("subsection") ? 1 : 0);
            if (tag == "h1") continue;
            if (tag is "h2" or "h3" or "h4")
            {
                var heading = Normalize(RenderInline(child));
                var level = Math.Max(1, tag[1] - '0' - 1) + Math.Max(0, childDepth - 1);
                if (heading.Length > 0) output.Add(new string('#', level) + " " + heading);
            }
            else if (tag == "pre" || child.ClassList.Contains("signature-CS"))
            {
                var code = child.TextContent.Trim('\r', '\n', ' ', '\t');
                if (code.Length > 0) output.Add("```csharp\n" + code + "\n```");
            }
            else if (tag == "table")
            {
                if (child.ClassList.Contains("list"))
                {
                    foreach (var row in child.QuerySelectorAll("tr"))
                    {
                        var cells = row.QuerySelectorAll("td").Select(RenderInline).Select(Normalize).Where(value => value.Length > 0).ToArray();
                        if (cells.Length > 0) output.Add("- " + string.Join(" — ", cells));
                    }
                }
            }
            else if (tag is "ul" or "ol")
            {
                foreach (var item in child.Children.Where(element => element.TagName.Equals("LI", StringComparison.OrdinalIgnoreCase)))
                {
                    var text = Normalize(RenderInline(item));
                    if (text.Length > 0) output.Add("- " + text);
                }
            }
            else if (tag == "p")
            {
                var raw = RenderInline(child);
                var text = serviceHeader ? NormalizeServiceLine(raw) : Normalize(raw);
                if (text.Length > 0 && NormalizeServiceLine(raw) != "/") output.Add(text);
            }
            else if (tag is "dl")
            {
                foreach (var item in child.Children)
                {
                    var text = Normalize(RenderInline(item));
                    if (text.Length > 0) output.Add((item.TagName == "DT" ? "- " : "  ") + text);
                }
            }
            else
            {
                var directText = string.Concat(child.ChildNodes.OfType<IText>().Select(node => node.Data));
                var text = Normalize(directText);
                if (text.Length > 0) output.Add(text);
                RenderChildren(child, output, cancellationToken, childDepth, serviceHeader);
            }
        }
    }

    private static string RenderInline(IElement element)
    {
        var builder = new StringBuilder();
        AppendInline(element, builder, false);
        return builder.ToString();
    }

    private static void AppendInline(INode node, StringBuilder builder, bool inCode)
    {
        if (node is IText text)
        {
            builder.Append(text.Data);
            return;
        }
        if (node is not IElement element || element.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase)) return;
        if (element.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase)) { builder.Append('\n'); return; }
        var code = inCode || element.TagName.Equals("CODE", StringComparison.OrdinalIgnoreCase);
        if (!inCode && code) builder.Append('`');
        foreach (var child in element.ChildNodes) AppendInline(child, builder, code);
        if (!inCode && code) builder.Append('`');
    }

    private static string Normalize(string? value) => value is null ? string.Empty : TextNormalization.Normalize(value);

    private static string NormalizeServiceLine(string value)
    {
        var collapsed = Regex.Replace(value, @"\s+", " ").Trim();
        collapsed = Regex.Replace(collapsed, @"\b(Implemented in|Inherits from):\s*", "$1: ", RegexOptions.IgnoreCase);
        return Normalize(collapsed);
    }
}
