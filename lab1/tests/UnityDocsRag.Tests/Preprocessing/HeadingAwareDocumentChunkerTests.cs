using UnityDocsRag.Core.Configuration;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Tests.Preprocessing;

public sealed class HeadingAwareDocumentChunkerTests
{
    [Fact]
    public async Task ChunksAreDeterministicOrderedSectionAwareNonEmptyAndOverlapped()
    {
        var document = Processed("# API\n\n## Description\n\n" + string.Join(' ', Enumerable.Range(0, 32).Select(i => "word" + i)));
        var chunker = new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 8, ChunkOverlap = 2 });
        var first = await chunker.ChunkAsync(document, CancellationToken.None);
        var second = await chunker.ChunkAsync(document, CancellationToken.None);
        Assert.Equal(first.Select(chunk => chunk.Text), second.Select(chunk => chunk.Text));
        Assert.Equal(first.Select(chunk => chunk.ChunkId), second.Select(chunk => chunk.ChunkId));
        Assert.Equal(Enumerable.Range(0, first.Count), first.Select(chunk => chunk.Ordinal));
        Assert.All(first, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Text)));
        Assert.All(first, chunk => Assert.StartsWith("# API", FirstNonEmptyLine(chunk.Text)));
        Assert.All(first, chunk => Assert.Equal(1, CountLines(chunk.Text, "## Description")));
        Assert.All(first, chunk => Assert.Equal(1, CountLines(chunk.Text, "# API")));
        Assert.All(first, chunk => Assert.True(chunk.ApproximateTokenCount <= 8));
        Assert.All(first, chunk => Assert.Equal("Description", chunk.Section));
        Assert.Contains(first.Zip(first.Skip(1)), pair => TokenSet(Body(pair.First)).Intersect(TokenSet(Body(pair.Second))).Count() is > 0 and <= 2);
        Assert.DoesNotContain(first, chunk => TokenSet(Body(chunk)).All(token => first.Take(chunk.Ordinal).Any(previous => TokenSet(Body(previous)).Contains(token))));
        Assert.All(first, chunk => Assert.Equal(chunk.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, chunk.ApproximateTokenCount));
    }

    [Fact]
    public async Task NearFullParagraphsKeepChunksWithinLimitAndTrimOverlapToAvailableSpace()
    {
        var document = Processed("## Description\n\n" + string.Join(' ', Enumerable.Range(0, 7).Select(i => "first" + i)) +
            "\n\n" + string.Join(' ', Enumerable.Range(0, 7).Select(i => "second" + i)));
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 8, ChunkOverlap = 2 })
            .ChunkAsync(document, CancellationToken.None);
        Assert.All(chunks, chunk => Assert.True(chunk.ApproximateTokenCount <= 8));
        Assert.Equal(8, chunks.Max(chunk => chunk.ApproximateTokenCount));
        Assert.Contains(TokenSet(Body(chunks[0])), token => TokenSet(Body(chunks[1])).Contains(token));
        Assert.DoesNotContain(chunks, chunk => TokenSet(Body(chunk)).All(token => TokenSet(Body(chunks[0])).Contains(token)) && chunk.Ordinal > 0);
    }

    [Fact]
    public async Task SectionTransitionNeverCarriesOldSectionTextIntoNewSection()
    {
        var document = Processed("## Description\n\nOnlyDescriptionWord\n\n## Properties\n\nPropertyValueWord");
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 8, ChunkOverlap = 2 })
            .ChunkAsync(document, CancellationToken.None);
        var properties = Assert.Single(chunks, chunk => chunk.Section == "Properties");
        Assert.Contains("PropertyValueWord", properties.Text);
        Assert.DoesNotContain("OnlyDescriptionWord", properties.Text);
    }

    [Fact]
    public async Task PreambleIsMergedIntoFirstRealSectionWithoutTitleOnlyChunk()
    {
        var document = Processed("# NavMeshAgent\n\nclass in UnityEngine.AI\n\nImplemented in: UnityEngine.AIModule\n\n## Description\n\nA navigation agent.", "NavMeshAgent");
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 20, ChunkOverlap = 2 })
            .ChunkAsync(document, CancellationToken.None);
        var first = Assert.Single(chunks);
        Assert.Equal("Description", first.Section);
        Assert.Contains("# NavMeshAgent", first.Text);
        Assert.Contains("class in UnityEngine.AI", first.Text);
        Assert.Contains("## Description", first.Text);
        Assert.Contains("A navigation agent.", first.Text);
        Assert.DoesNotContain(chunks, chunk => chunk.Text.Trim() == "# NavMeshAgent");
        Assert.Equal(1, CountLines(first.Text, "# NavMeshAgent"));
        Assert.Equal(1, CountLines(first.Text, "## Description"));
        Assert.True(first.ApproximateTokenCount <= 20);
    }

    [Fact]
    public async Task PreambleRemainsWhenDocumentHasNoMarkdownSections()
    {
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 20, ChunkOverlap = 2 })
            .ChunkAsync(Processed("# TypeName\n\nstruct in UnityEngine", "TypeName"), CancellationToken.None);
        var chunk = Assert.Single(chunks);
        Assert.Null(chunk.Section);
        Assert.Contains("# TypeName", chunk.Text);
        Assert.Contains("struct in UnityEngine", chunk.Text);
    }

    [Fact]
    public async Task EmptySectionIsSkippedAndOrdinalsRemainContiguous()
    {
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 10, ChunkOverlap = 1 })
            .ChunkAsync(Processed("## Empty\n\n## Delegates\n\nShortDelegate"), CancellationToken.None);
        var delegateChunk = Assert.Single(chunks);
        Assert.Equal("Delegates", delegateChunk.Section);
        Assert.Contains("ShortDelegate", delegateChunk.Text);
        Assert.DoesNotContain(chunks, chunk => chunk.Text.Trim() == "## Empty");
        Assert.Equal(new[] { 0 }, chunks.Select(chunk => chunk.Ordinal));
    }

    [Fact]
    public async Task NestedHeadingAndDataKeepInheritedMembersAsTheChunkSection()
    {
        var document = Processed("## Inherited Members\n\n### Properties\n\n- enabled — inherited value\n\n### Public Methods\n\n- GetComponent — inherited method");
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 20, ChunkOverlap = 2 })
            .ChunkAsync(document, CancellationToken.None);
        var chunk = Assert.Single(chunks);
        Assert.Equal("Inherited Members", chunk.Section);
        Assert.Contains("### Properties", chunk.Text);
        Assert.Contains("enabled", chunk.Text);
        Assert.Contains("### Public Methods", chunk.Text);
        Assert.Contains("GetComponent", chunk.Text);
        Assert.True(chunk.ApproximateTokenCount <= 20);
    }

    [Fact]
    public async Task OverlapIsResetAtSectionBoundary()
    {
        var document = Processed("## Description\n\nDescriptionUnique\n\n## Properties\n\nPropertyUnique");
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 8, ChunkOverlap = 2 })
            .ChunkAsync(document, CancellationToken.None);
        var properties = Assert.Single(chunks, chunk => chunk.Section == "Properties");
        Assert.DoesNotContain("DescriptionUnique", properties.Text);
        Assert.All(chunks, chunk => Assert.True(chunk.ApproximateTokenCount <= 8));
    }

    [Fact]
    public async Task LongSectionIsSplitAndSmallCodeBlockStaysTogether()
    {
        var document = Processed("# API\n\n## Example\n\n" + string.Join(' ', Enumerable.Range(0, 25).Select(i => "word" + i)) +
            "\n\n```csharp\npublic void Run()\n{\n    Call();\n}\n```");
        var chunks = await new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 14, ChunkOverlap = 1 })
            .ChunkAsync(document, CancellationToken.None);
        Assert.True(chunks.Count > 2);
        Assert.Contains(chunks, chunk => chunk.Text.Contains("```csharp\npublic void Run()\n{\n    Call();\n}\n```", StringComparison.Ordinal));
        Assert.All(chunks, chunk => Assert.True(chunk.ApproximateTokenCount <= 14));
    }

    [Fact]
    public async Task ContextThatLeavesNoBodyBudgetIsRejectedClearly()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = 4, ChunkOverlap = 1 })
                .ChunkAsync(Processed("## Inherited Members\n\nvalue"), CancellationToken.None));
        Assert.Contains("cannot fit the document title/section context", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, -1)]
    [InlineData(5, 5)]
    public void InvalidOptionsAreRejected(int size, int overlap) =>
        Assert.ThrowsAny<ArgumentException>(() => new HeadingAwareDocumentChunker(new ChunkingOptions { ChunkSize = size, ChunkOverlap = overlap }));

    [Fact]
    public async Task CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HeadingAwareDocumentChunker(new ChunkingOptions()).ChunkAsync(Processed("# T\n\ntext"), cts.Token));
    }

    private static ProcessedDocument Processed(string text, string title = "API") => new("doc", new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/A.html"),
        title, "6000.3", text, "hash", DateTimeOffset.UnixEpoch);

    private static HashSet<string> TokenSet(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    private static string FirstNonEmptyLine(string text) => text.Split('\n').First(line => !string.IsNullOrWhiteSpace(line));
    private static int CountLines(string text, string expected) => text.Split('\n').Count(line => line == expected);
    private static string Body(DocumentChunk chunk)
    {
        var lines = chunk.Text.Split('\n');
        var sectionLine = Array.FindIndex(lines, line => line.StartsWith("## ", StringComparison.Ordinal));
        return sectionLine < 0 ? string.Join('\n', lines.Skip(2)) : string.Join('\n', lines.Skip(sectionLine + 1));
    }
}
