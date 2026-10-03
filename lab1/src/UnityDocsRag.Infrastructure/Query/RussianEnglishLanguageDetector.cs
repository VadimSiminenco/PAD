using System.Text.RegularExpressions;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Infrastructure.Query;

/// <summary>Conservative, local script and vocabulary based detection for Russian and English.</summary>
public sealed class RussianEnglishLanguageDetector : ILanguageDetector
{
    private static readonly HashSet<string> EnglishIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "can", "do", "does", "for", "get", "how", "i", "in", "is", "it",
        "method", "of", "on", "the", "to", "use", "using", "what", "when", "where", "which", "why", "with"
    };

    private static readonly Regex WordPattern = new("[A-Za-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnityIdentifierPattern = new(
        @"\b[A-Z][A-Za-z0-9]*\.[A-Z][A-Za-z0-9]*\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<SupportedLanguage?> DetectAsync(UserQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        cancellationToken.ThrowIfCancellationRequested();

        var text = question.Text;
        foreach (var character in text)
        {
            if (IsUnsupportedScript(character)) return Task.FromResult<SupportedLanguage?>(null);
            if ("іїєґІЇЄҐ".IndexOf(character) >= 0) return Task.FromResult<SupportedLanguage?>(null);
        }

        if (text.Any(IsCyrillic)) return Task.FromResult<SupportedLanguage?>(SupportedLanguage.Russian);
        if (UnityIdentifierPattern.IsMatch(text) || WordPattern.Matches(text).Any(match => EnglishIndicators.Contains(match.Value)))
            return Task.FromResult<SupportedLanguage?>(SupportedLanguage.English);

        return Task.FromResult<SupportedLanguage?>(null);
    }

    private static bool IsCyrillic(char character) => character is >= '\u0400' and <= '\u052F';

    private static bool IsUnsupportedScript(char character) =>
        character is >= '\u0370' and <= '\u03FF' || // Greek
        character is >= '\u0600' and <= '\u06FF' || // Arabic
        character is >= '\u4E00' and <= '\u9FFF' || // CJK Unified Ideographs
        character is >= '\u3400' and <= '\u4DBF' || // CJK Extension A
        character is >= '\u3040' and <= '\u30FF' || // Hiragana and Katakana
        character is >= '\uAC00' and <= '\uD7AF';   // Hangul
}
