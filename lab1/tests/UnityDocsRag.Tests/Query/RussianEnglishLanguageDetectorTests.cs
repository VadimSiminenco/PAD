using UnityDocsRag.Core.Generation;
using UnityDocsRag.Infrastructure.Query;

namespace UnityDocsRag.Tests.Query;

public sealed class RussianEnglishLanguageDetectorTests
{
    private readonly RussianEnglishLanguageDetector _detector = new();

    [Theory]
    [InlineData("Как использовать агент навигации?")]
    [InlineData("Как задать точку назначения для NavMeshAgent.SetDestination?")]
    public async Task DetectsRussianIncludingQuestionsWithLatinApiIdentifiers(string text)
    {
        Assert.Equal(SupportedLanguage.Russian,
            await _detector.DetectAsync(new UserQuestion(text), CancellationToken.None));
    }

    [Theory]
    [InlineData("How do I set a destination?")]
    [InlineData("What does NavMeshAgent do?")]
    public async Task DetectsEnglishQuestions(string text)
    {
        Assert.Equal(SupportedLanguage.English,
            await _detector.DetectAsync(new UserQuestion(text), CancellationToken.None));
    }

    [Theory]
    [InlineData("如何设置目标？")]
    [InlineData("Πώς μπορώ να το χρησιμοποιήσω;")]
    [InlineData("كيف أستخدم هذا؟")]
    [InlineData("Як встановити ціль?")]
    [InlineData("Comment utiliser ceci?")]
    [InlineData("Cum pot seta destinația?")]
    [InlineData("NavMeshAgent")]
    public async Task LeavesUnsupportedOrAmbiguousTextUndetected(string text)
    {
        Assert.Null(await _detector.DetectAsync(new UserQuestion(text), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeDetection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _detector.DetectAsync(new UserQuestion("How do I use this?"), cancellation.Token));
    }
}
