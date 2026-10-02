using System.Text;
using System.Text.RegularExpressions;

namespace UnityDocsRag.Infrastructure.Preprocessing;

public static class TextNormalization
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var result = new StringBuilder();
        var inFence = false;
        var blankCount = 0;
        foreach (var raw in lines)
        {
            var trimmedEnd = raw.TrimEnd();
            var fenceLine = trimmedEnd.TrimStart().StartsWith("```", StringComparison.Ordinal);
            var line = inFence ? trimmedEnd : Regex.Replace(trimmedEnd, @"[\t \u00A0]+", " ");
            if (!inFence && string.IsNullOrWhiteSpace(line))
            {
                if (result.Length == 0 || blankCount >= 2) continue;
                blankCount++;
            }
            else blankCount = 0;
            if (result.Length > 0) result.Append('\n');
            result.Append(line);
            if (fenceLine) inFence = !inFence;
        }
        return result.ToString().Trim(' ', '\n', '\t');
    }
}
