using System.Text.Json;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;
using UnityDocsRag.Evaluation;

namespace UnityDocsRag.Tests.Evaluation;

public sealed class AskEvaluationRunnerTests
{
    private const string DatasetJson = """
        {
          "Version": 1,
          "UnityVersion": "6000.3",
          "Questions": [
            {
              "Id": "Q1", "Language": "ru", "Category": "InCorpus",
              "Text": "Первый вопрос?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": ["https://example.test/source"],
              "ExpectedFacts": ["Fact for manual review only"]
            },
            {
              "Id": "Q2", "Language": "en", "Category": "InCorpus",
              "Text": "Second question?", "ExpectedStatus": "Answered",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": ["https://example.test/second"],
              "ExpectedFacts": ["Another fact"]
            },
            {
              "Id": "Q3", "Language": "ru", "Category": "UnityWithoutCorpusEvidence",
              "Text": "Третий вопрос?", "ExpectedStatus": "InsufficientEvidence",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            },
            {
              "Id": "Q4", "Language": "en", "Category": "OutOfDomain",
              "Text": "Fourth question?", "ExpectedStatus": "OutOfDomain",
              "RequiresMultipleSources": false,
              "ExpectedSourceUrls": [], "ExpectedFacts": []
            }
          ]
        }
        """;

    private static readonly EvaluationDataset Dataset = EvaluationDataset.Parse(DatasetJson);

    [Fact]
    public async Task EvaluatesEveryQuestionOnceAndSummarizesExactStatusMatchesByCategory()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var service = new FakeQueryService((question, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            calls.Add(question.Text);
            return Task.FromResult(question.Text switch
            {
                "Первый вопрос?" => new RagAnswer("Grounded answer", SupportedLanguage.Russian,
                    AnswerStatus.Answered, [new Citation("Source", new Uri("https://example.test/source"))]),
                "Second question?" => new RagAnswer("Outside domain", SupportedLanguage.English,
                    AnswerStatus.OutOfDomain),
                "Третий вопрос?" => new RagAnswer("Недостаточно данных", SupportedLanguage.Russian,
                    AnswerStatus.InsufficientEvidence),
                _ => new RagAnswer("Insufficient", null,
                    AnswerStatus.InsufficientEvidence)
            });
        });

        var report = await new AskEvaluationRunner(service).RunAsync(Dataset, cancellation.Token);

        Assert.Equal(4, service.Calls);
        Assert.Equal(["Первый вопрос?", "Second question?", "Третий вопрос?", "Fourth question?"], calls);
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], report.Questions.Select(result => result.Id));
        Assert.Equal(1, report.DatasetVersion);
        Assert.Equal("6000.3", report.UnityVersion);
        Assert.Equal(["Answered", "OutOfDomain", "InsufficientEvidence", "InsufficientEvidence"],
            report.Questions.Select(result => result.ActualStatus));
        Assert.Equal([true, false, true, false], report.Questions.Select(result => result.StatusMatches));
        Assert.Equal("ru", report.Questions[0].ExpectedLanguage);
        Assert.Equal("ru", report.Questions[0].DetectedLanguage);
        Assert.Equal("Grounded answer", report.Questions[0].AnswerText);
        Assert.Equal(["https://example.test/source"], report.Questions[0].CitationUrls);
        Assert.Equal(["https://example.test/source"], report.Questions[0].ExpectedSourceUrls);
        Assert.Equal(["Fact for manual review only"], report.Questions[0].ExpectedFacts);
        Assert.True(report.Questions[0].StatusMatches); // Facts are not compared literally.
        Assert.Equal("en", report.Questions[1].ExpectedLanguage);
        Assert.Equal("en", report.Questions[1].DetectedLanguage);
        Assert.Empty(report.Questions[1].CitationUrls);
        Assert.Equal("en", report.Questions[3].ExpectedLanguage);
        Assert.Null(report.Questions[3].DetectedLanguage);

        Assert.Equal(4, report.Summary.QuestionCount);
        Assert.Equal(2, report.Summary.MatchedStatusCount);
        Assert.Equal(0.5, report.Summary.StatusMatchRate);
        Assert.Equal(3, report.Summary.ByExpectedCategory.Count);
        AssertCategory(report, "InCorpus", 2, 1, 0.5);
        AssertCategory(report, "UnityWithoutCorpusEvidence", 1, 1, 1.0);
        AssertCategory(report, "OutOfDomain", 1, 0, 0.0);

        var json = JsonSerializer.Serialize(report);
        Assert.Contains("\"ExpectedFacts\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connection string", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chunk text", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vector", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullAnswerIsRejected()
    {
        var service = new FakeQueryService((_, _) => Task.FromResult<RagAnswer>(null!));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AskEvaluationRunner(service).RunAsync(Dataset, CancellationToken.None));
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task ExistingOutputIsNotOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unitydocs-ask-evaluation-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "existing report");
            var report = MakeEmptyReport();

            await Assert.ThrowsAsync<IOException>(() =>
                AskEvaluationReportWriter.WriteNewAsync(report, path, CancellationToken.None));

            Assert.Equal("existing report", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NewOutputContainsReportWithoutAddingHiddenData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unitydocs-ask-evaluation-{Guid.NewGuid():N}.json");
        try
        {
            await AskEvaluationReportWriter.WriteNewAsync(MakeEmptyReport(), path, CancellationToken.None);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            Assert.Equal(1, json.RootElement.GetProperty("DatasetVersion").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("Summary").GetProperty("QuestionCount").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResumesAfterTwoSavedQuestionsWithoutCallingServiceAgainForThem()
    {
        using var files = new TempRunFiles();
        var firstProgress = new List<string>();
        var firstService = new FakeQueryService((question, _) =>
            question.Text == "Третий вопрос?"
                ? Task.FromException<RagAnswer>(new InvalidOperationException("synthetic failure"))
                : Task.FromResult(ExpectedAnswer(question.Text)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AskEvaluationRunner(firstService).RunResumableAsync(Dataset,
                files.DatasetPath, files.ConfigPath, files.OutputPath, firstProgress.Add, CancellationToken.None));

        Assert.Equal(3, firstService.Calls);
        Assert.Equal(["Q1 (1/4): Answered", "Q2 (2/4): Answered"], firstProgress);
        using (var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(files.CheckpointPath)))
        {
            Assert.Equal(2, checkpoint.RootElement.GetProperty("CompletedQuestions").GetArrayLength());
        }

        var resumedQuestions = new List<string>();
        var secondProgress = new List<string>();
        var secondService = new FakeQueryService((question, _) =>
        {
            resumedQuestions.Add(question.Text);
            return Task.FromResult(ExpectedAnswer(question.Text));
        });
        var report = await new AskEvaluationRunner(secondService).RunResumableAsync(Dataset,
            files.DatasetPath, files.ConfigPath, files.OutputPath, secondProgress.Add, CancellationToken.None);

        Assert.Equal(2, secondService.Calls);
        Assert.Equal(["Третий вопрос?", "Fourth question?"], resumedQuestions);
        Assert.Equal(["Q3 (3/4): InsufficientEvidence", "Q4 (4/4): OutOfDomain"], secondProgress);
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], report.Questions.Select(result => result.Id));
        Assert.Equal(4, report.Summary.QuestionCount);
        Assert.Equal(4, report.Summary.MatchedStatusCount);
        Assert.Equal(1.0, report.Summary.StatusMatchRate);
        AssertCategory(report, "InCorpus", 2, 2, 1.0);
        AssertCategory(report, "UnityWithoutCorpusEvidence", 1, 1, 1.0);
        AssertCategory(report, "OutOfDomain", 1, 1, 1.0);
        await AskEvaluationReportWriter.WriteNewAsync(report, files.OutputPath, CancellationToken.None);
        Assert.True(File.Exists(files.OutputPath));

        var checkpointJson = await File.ReadAllTextAsync(files.CheckpointPath);
        Assert.DoesNotContain("connection string", checkpointJson, StringComparison.Ordinal);
        Assert.DoesNotContain("chunk text", checkpointJson, StringComparison.Ordinal);
        Assert.DoesNotContain("vector", checkpointJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedConfigOrQuestionIdsRejectResumeWithoutModifyingCheckpoint()
    {
        using var files = new TempRunFiles();
        await SaveTwoQuestionCheckpointAsync(files);
        var originalCheckpoint = await File.ReadAllBytesAsync(files.CheckpointPath);
        await File.WriteAllTextAsync(files.ConfigPath, "changed config");
        var fake = new FakeQueryService((_, _) => throw new InvalidOperationException("Must not be called."));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AskEvaluationRunner(fake).RunResumableAsync(Dataset,
                files.DatasetPath, files.ConfigPath, files.OutputPath, null, CancellationToken.None));
        Assert.Equal(0, fake.Calls);
        Assert.Equal(originalCheckpoint, await File.ReadAllBytesAsync(files.CheckpointPath));

        await File.WriteAllTextAsync(files.ConfigPath, "same config");
        await File.WriteAllTextAsync(files.DatasetPath, DatasetJson + " ");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AskEvaluationRunner(fake).RunResumableAsync(Dataset,
                files.DatasetPath, files.ConfigPath, files.OutputPath, null, CancellationToken.None));
        Assert.Equal(0, fake.Calls);
        Assert.Equal(originalCheckpoint, await File.ReadAllBytesAsync(files.CheckpointPath));

        await File.WriteAllTextAsync(files.DatasetPath, DatasetJson);
        var changedIds = EvaluationDataset.Parse(DatasetJson.Replace(
            "\"Id\": \"Q2\"", "\"Id\": \"QX\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AskEvaluationRunner(fake).RunResumableAsync(changedIds,
                files.DatasetPath, files.ConfigPath, files.OutputPath, null, CancellationToken.None));
        Assert.Equal(0, fake.Calls);
        Assert.Equal(originalCheckpoint, await File.ReadAllBytesAsync(files.CheckpointPath));
    }

    [Fact]
    public async Task ExistingFinalOutputPreventsResumeAndIsNotOverwritten()
    {
        using var files = new TempRunFiles();
        await SaveTwoQuestionCheckpointAsync(files);
        var originalCheckpoint = await File.ReadAllBytesAsync(files.CheckpointPath);
        await File.WriteAllTextAsync(files.OutputPath, "existing final report");
        var fake = new FakeQueryService((_, _) => throw new InvalidOperationException("Must not be called."));

        await Assert.ThrowsAsync<IOException>(() =>
            new AskEvaluationRunner(fake).RunResumableAsync(Dataset,
                files.DatasetPath, files.ConfigPath, files.OutputPath, null, CancellationToken.None));

        Assert.Equal(0, fake.Calls);
        Assert.Equal("existing final report", await File.ReadAllTextAsync(files.OutputPath));
        Assert.Equal(originalCheckpoint, await File.ReadAllBytesAsync(files.CheckpointPath));
    }

    private static async Task SaveTwoQuestionCheckpointAsync(TempRunFiles files)
    {
        var fake = new FakeQueryService((question, _) =>
            question.Text == "Третий вопрос?"
                ? Task.FromException<RagAnswer>(new InvalidOperationException("synthetic failure"))
                : Task.FromResult(ExpectedAnswer(question.Text)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AskEvaluationRunner(fake).RunResumableAsync(Dataset,
                files.DatasetPath, files.ConfigPath, files.OutputPath, null, CancellationToken.None));
    }

    private static RagAnswer ExpectedAnswer(string question) => question switch
    {
        "Первый вопрос?" => new RagAnswer("First answer", SupportedLanguage.Russian,
            AnswerStatus.Answered, [new Citation("Source", new Uri("https://example.test/source"))]),
        "Second question?" => new RagAnswer("Second answer", SupportedLanguage.English,
            AnswerStatus.Answered, [new Citation("Source", new Uri("https://example.test/second"))]),
        "Третий вопрос?" => new RagAnswer("Недостаточно данных", SupportedLanguage.Russian,
            AnswerStatus.InsufficientEvidence),
        _ => new RagAnswer("Outside domain", SupportedLanguage.English, AnswerStatus.OutOfDomain)
    };

    private sealed class TempRunFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            $"unitydocs-ask-resume-{Guid.NewGuid():N}");

        public TempRunFiles()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(DatasetPath, DatasetJson);
            File.WriteAllText(ConfigPath, "same config");
        }

        public string DatasetPath => Path.Combine(_directory, "questions.json");
        public string ConfigPath => Path.Combine(_directory, "ask.json");
        public string OutputPath => Path.Combine(_directory, "report.json");
        public string CheckpointPath => OutputPath + ".checkpoint.json";

        public void Dispose()
        {
            File.Delete(DatasetPath);
            File.Delete(ConfigPath);
            File.Delete(OutputPath);
            File.Delete(CheckpointPath);
            Directory.Delete(_directory);
        }
    }

    private static AskEvaluationReport MakeEmptyReport() => new(1, "6000.3",
        Array.Empty<AskQuestionEvaluation>(), new AskEvaluationSummary(0, 0, 0,
            Array.Empty<AskCategorySummary>()));

    private static void AssertCategory(AskEvaluationReport report, string category,
        int count, int matched, double rate)
    {
        var summary = Assert.Single(report.Summary.ByExpectedCategory,
            item => item.ExpectedCategory == category);
        Assert.Equal(count, summary.QuestionCount);
        Assert.Equal(matched, summary.MatchedStatusCount);
        Assert.Equal(rate, summary.StatusMatchRate);
    }

    private sealed class FakeQueryService(Func<UserQuestion, CancellationToken, Task<RagAnswer>> answer)
        : IRagQueryService
    {
        public int Calls { get; private set; }

        public Task<RagAnswer> AnswerAsync(UserQuestion question, CancellationToken cancellationToken)
        {
            Calls++;
            return answer(question, cancellationToken);
        }
    }
}
