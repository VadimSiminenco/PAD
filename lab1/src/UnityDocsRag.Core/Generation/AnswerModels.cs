using System.Collections.ObjectModel;

namespace UnityDocsRag.Core.Generation;

public enum SupportedLanguage
{
    Russian,
    English
}

public enum AnswerStatus
{
    Answered,
    OutOfDomain,
    UnsupportedLanguage,
    InsufficientEvidence,
    Failed
}

public sealed record UserQuestion
{
    public UserQuestion(string text)
    {
        Text = string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException("Question must not be empty.", nameof(text))
            : text;
    }

    public string Text { get; }
}

public sealed record Citation
{
    public Citation(string title, Uri url, string? section = null)
    {
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Citation title must not be empty.", nameof(title))
            : title;
        Url = url is { IsAbsoluteUri: true }
            ? url
            : throw new ArgumentException("Citation URL must be absolute.", nameof(url));
        Section = string.IsNullOrWhiteSpace(section) ? null : section;
    }

    public string Title { get; }
    public Uri Url { get; }
    public string? Section { get; }
}

public sealed record RagAnswer
{
    public RagAnswer(
        string? text,
        SupportedLanguage? language,
        AnswerStatus status,
        IEnumerable<Citation>? citations = null)
    {
        var materializedCitations = Array.AsReadOnly((citations ?? Array.Empty<Citation>()).ToArray());

        if (status == AnswerStatus.Answered && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("An answered result must contain answer text.", nameof(text));
        }

        if (status == AnswerStatus.Answered && (language is null || !Enum.IsDefined(language.Value)))
        {
            throw new ArgumentException("An answered result must have a supported language.", nameof(language));
        }

        if (status == AnswerStatus.Answered && materializedCitations.Count == 0)
        {
            throw new ArgumentException("An answered result must contain at least one citation.", nameof(citations));
        }

        if (materializedCitations.Any(citation => citation is null))
        {
            throw new ArgumentException("Citations cannot contain null values.", nameof(citations));
        }

        Text = text;
        Language = language;
        Status = status;
        Citations = materializedCitations;
    }

    public string? Text { get; }
    public SupportedLanguage? Language { get; }
    public AnswerStatus Status { get; }
    public IReadOnlyList<Citation> Citations { get; }
}
