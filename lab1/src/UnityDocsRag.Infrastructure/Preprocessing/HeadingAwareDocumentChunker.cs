using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Configuration;
using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Infrastructure.Preprocessing;

public sealed class HeadingAwareDocumentChunker : IDocumentChunker
{
    private readonly int _size;
    private readonly int _overlap;

    public HeadingAwareDocumentChunker(ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ChunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(options.ChunkSize));
        if (options.ChunkOverlap < 0 || options.ChunkOverlap >= options.ChunkSize) throw new ArgumentOutOfRangeException(nameof(options.ChunkOverlap));
        _size = options.ChunkSize;
        _overlap = options.ChunkOverlap;
    }

    public Task<IReadOnlyList<DocumentChunk>> ChunkAsync(ProcessedDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var sections = new List<SectionContent>();
        var preamble = new List<string>();
        SectionContent? section = null;
        var paragraph = new StringBuilder();
        var inFence = false;
        foreach (var line in document.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                section = new SectionContent(line[3..].Trim());
                sections.Add(section);
                continue;
            }
            if (!inFence && string.Equals(line.Trim(), "# " + document.Title, StringComparison.Ordinal))
            {
                FlushParagraph();
                // Document title is emitted as context for every chunk, never retained as body text.
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (string.IsNullOrWhiteSpace(line) && !inFence) FlushParagraph();
            else
            {
                if (paragraph.Length > 0) paragraph.Append('\n');
                paragraph.Append(line);
            }
        }
        FlushParagraph();

        var nonEmptySections = sections.Where(candidate => candidate.Units.Count > 0).ToList();
        if (nonEmptySections.Count > 0)
        {
            nonEmptySections[0].Units.InsertRange(0, preamble);
            sections = nonEmptySections;
        }
        else sections = preamble.Count == 0 ? new List<SectionContent>() : new List<SectionContent> { new(null, preamble) };

        var chunks = new List<DocumentChunk>();
        foreach (var sectionContent in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contextPrefix = BuildContextPrefix(document.Title, sectionContent.Name);
            var prefixTokens = CountTokens(contextPrefix);
            var bodyBudget = _size - prefixTokens;
            if (bodyBudget < 1)
                throw new InvalidOperationException($"ChunkSize {_size} cannot fit the document title/section context and one body token.");
            var current = new List<string>();
            var currentCount = 0;
            var currentContainsOnlyOverlap = false;
            foreach (var rawUnit in sectionContent.Units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var unit in SplitIfNeeded(rawUnit, bodyBudget, cancellationToken))
                {
                    var count = CountTokens(unit);
                    if (current.Count > 0 && currentCount + count > bodyBudget)
                    {
                        if (currentContainsOnlyOverlap)
                        {
                            current.Clear();
                            currentCount = 0;
                        }
                        else
                        {
                            var previousChunk = string.Join("\n\n", current);
                            AddChunk(contextPrefix, previousChunk, sectionContent.Name, chunks, document.DocumentId);
                            var overlap = GetOverlap(previousChunk, Math.Min(_overlap, Math.Max(0, bodyBudget - count)));
                            current = overlap.Length == 0 ? new List<string>() : new List<string> { overlap };
                            currentCount = CountTokens(overlap);
                            currentContainsOnlyOverlap = overlap.Length > 0;
                        }
                    }
                    current.Add(unit);
                    currentCount += count;
                    currentContainsOnlyOverlap = false;
                }
            }
            if (current.Count > 0) AddChunk(contextPrefix, string.Join("\n\n", current), sectionContent.Name, chunks, document.DocumentId);
        }
        return Task.FromResult<IReadOnlyList<DocumentChunk>>(chunks.AsReadOnly());

        void FlushParagraph()
        {
            var text = paragraph.ToString().Trim();
            if (text.Length > 0)
            {
                if (section is null) preamble.Add(text);
                else section.Units.Add(text);
            }
            paragraph.Clear();
        }
    }

    private IEnumerable<string> SplitIfNeeded(string text, int bodyBudget, CancellationToken cancellationToken)
    {
        if (CountTokens(text) <= bodyBudget)
        {
            yield return text;
            yield break;
        }

        // Approximate tokens are non-whitespace runs. Oversized paragraphs/code split only between runs.
        var words = Regex.Matches(text, @"\S+").Select(match => match.Value).ToArray();
        var capacity = Math.Max(1, bodyBudget - _overlap);
        for (var offset = 0; offset < words.Length; offset += capacity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return string.Join(' ', words.Skip(offset).Take(capacity));
        }
    }

    private static string GetOverlap(string text, int overlap)
    {
        if (overlap <= 0) return string.Empty;
        var words = Regex.Matches(text, @"\S+").Select(match => match.Value).ToArray();
        return string.Join(' ', words.TakeLast(overlap));
    }

    private static string BuildContextPrefix(string title, string? section)
    {
        var prefix = "# " + title.Trim();
        if (!string.IsNullOrWhiteSpace(section)) prefix += "\n\n## " + section.Trim();
        return prefix;
    }

    private static void AddChunk(string prefix, string body, string? section, List<DocumentChunk> output, string documentId)
    {
        body = body.Trim();
        if (body.Length == 0) return;
        var text = prefix + "\n\n" + body;
        var index = output.Count;
        var hashInput = $"{documentId}\n{index}\n{text}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
        output.Add(new DocumentChunk(id, documentId, text, section, index, CountTokens(text)));
    }

    private static int CountTokens(string text) => Regex.Matches(text, @"\S+").Count;
    private sealed class SectionContent(string? name, List<string>? units = null)
    {
        public string? Name { get; } = name;
        public List<string> Units { get; } = units ?? new List<string>();
    }
}
