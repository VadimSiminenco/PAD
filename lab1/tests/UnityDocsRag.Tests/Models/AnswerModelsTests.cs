using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Tests.Models;

public sealed class AnswerModelsTests
{
    private static Citation CreateCitation() => new(
        "GameObject",
        new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/GameObject.html"),
        "Description");

    [Fact]
    public void AnsweredWithoutCitationsIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new RagAnswer("A grounded answer", SupportedLanguage.English, AnswerStatus.Answered));
    }

    [Fact]
    public void AnsweredWithTextLanguageAndCitationIsCreated()
    {
        var citation = CreateCitation();

        var answer = new RagAnswer("A grounded answer", SupportedLanguage.English, AnswerStatus.Answered, [citation]);

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(SupportedLanguage.English, answer.Language);
        Assert.Same(citation, Assert.Single(answer.Citations));
    }

    [Fact]
    public void InsufficientEvidenceWithoutCitationsIsCreated()
    {
        var answer = new RagAnswer(null, null, AnswerStatus.InsufficientEvidence);

        Assert.Equal(AnswerStatus.InsufficientEvidence, answer.Status);
        Assert.Empty(answer.Citations);
    }

    [Fact]
    public void CitationsAreCopiedAtConstruction()
    {
        var citations = new List<Citation> { CreateCitation() };
        var answer = new RagAnswer("A grounded answer", SupportedLanguage.English, AnswerStatus.Answered, citations);
        citations.Add(new Citation("Transform", new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Transform.html")));

        Assert.Single(answer.Citations);
    }
}
