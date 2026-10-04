using UnityDocsRag.Core.Generation;
using UnityDocsRag.Evaluation;

namespace UnityDocsRag.Tests.Evaluation;

public sealed class EvaluationDatasetTests
{
    private const string ValidJson = """
        {
          "Version": 1,
          "UnityVersion": "6000.3",
          "Questions": [
            {
              "Id": "Q1", "Language": "ru", "Category": "InCorpus",
              "Text": "Как работает метод?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": true,
              "ExpectedSourceUrls": ["https://example.test/a", "https://example.test/b"],
              "ExpectedFacts": ["Первый факт"]
            },
            {
              "Id": "Q2", "Language": "en", "Category": "UnityWithoutCorpusEvidence",
              "Text": "How does another Unity method work?", "ExpectedStatus": "InsufficientEvidence",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            },
            {
              "Id": "Q3", "Language": "en", "Category": "OutOfDomain",
              "Text": "How do I bake bread?", "ExpectedStatus": "OutOfDomain",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            }
          ]
        }
        """;

    [Fact]
    public void ParsesValidDatasetWithoutRequiringTenQuestions()
    {
        var dataset = EvaluationDataset.Parse(ValidJson);

        Assert.Equal(1, dataset.Version);
        Assert.Equal("6000.3", dataset.UnityVersion);
        Assert.Equal(3, dataset.Questions.Count);
        Assert.Equal(EvaluationCategory.InCorpus, dataset.Questions[0].Category);
        Assert.Equal(AnswerStatus.Answered, dataset.Questions[0].ExpectedStatus);
        Assert.True(dataset.Questions[0].RequiresMultipleSources);
        Assert.Equal(2, dataset.Questions[0].ExpectedSourceUrls.Count);
        Assert.Single(dataset.Questions[0].ExpectedFacts);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void RejectsMalformedOrIncompleteJson(string json)
    {
        Assert.Throws<InvalidDataException>(() => EvaluationDataset.Parse(json));
    }

    [Theory]
    [InlineData("\"Version\": 1", "\"Version\": 2")]
    [InlineData("\"Id\": \"Q2\"", "\"Id\": \"q1\"")]
    [InlineData("\"Language\": \"ru\"", "\"Language\": \"fr\"")]
    [InlineData("\"Category\": \"InCorpus\"", "\"Category\": \"Unknown\"")]
    [InlineData("\"ExpectedStatus\": \"Answered\"", "\"ExpectedStatus\": \"OutOfDomain\"")]
    [InlineData("\"Text\": \"Как работает метод?\"", "\"Text\": \"   \"")]
    [InlineData("\"ExpectedSourceUrls\": [\"https://example.test/a\", \"https://example.test/b\"]", "\"ExpectedSourceUrls\": []")]
    [InlineData("\"ExpectedFacts\": [\"Первый факт\"]", "\"ExpectedFacts\": []")]
    [InlineData("\"RequiresMultipleSources\": true", "\"RequiresMultipleSources\": \"yes\"")]
    public void RejectsInvalidSchemaValues(string original, string replacement)
    {
        var json = ValidJson.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(ValidJson, json);

        Assert.Throws<InvalidDataException>(() => EvaluationDataset.Parse(json));
    }

    [Fact]
    public void RejectsRefusalWithExpectedFacts()
    {
        var json = ValidJson.Replace(
            "\"ExpectedSourceUrls\": [], \"ExpectedFacts\": []",
            "\"ExpectedSourceUrls\": [], \"ExpectedFacts\": [\"not allowed\"]",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => EvaluationDataset.Parse(json));
    }

    [Fact]
    public void RejectsRefusalWithExpectedUrls()
    {
        var json = ValidJson.Replace(
            "\"ExpectedSourceUrls\": [], \"ExpectedFacts\": []",
            "\"ExpectedSourceUrls\": [\"https://example.test/a\"], \"ExpectedFacts\": []",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => EvaluationDataset.Parse(json));
    }

    [Fact]
    public void RejectsMultipleSourcesFlagWithOnlyOneUrl()
    {
        var json = ValidJson.Replace(
            "[\"https://example.test/a\", \"https://example.test/b\"]",
            "[\"https://example.test/a\"]",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => EvaluationDataset.Parse(json));
    }

    [Fact]
    public void RejectsInvalidExpectedUrl()
    {
        var json = ValidJson.Replace("https://example.test/a", "not-a-url", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => EvaluationDataset.Parse(json));
    }
}
