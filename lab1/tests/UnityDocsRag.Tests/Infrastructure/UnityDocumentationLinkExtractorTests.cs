using UnityDocsRag.Infrastructure.Documentation;

namespace UnityDocsRag.Tests.Infrastructure;

public sealed class UnityDocumentationLinkExtractorTests
{
    private static readonly Uri IndexUrl = new(UnityDocumentationSourceOptions.DefaultBaseUrl);
    private readonly UnityDocumentationLinkExtractor _extractor = new();

    [Fact]
    public void FindsRelativeTocScript()
    {
        var url = _extractor.FindTocUrl("<script src='docdata/toc.js'></script>", IndexUrl);
        Assert.Equal("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/docdata/toc.js", url.AbsoluteUri);
    }

    [Theory]
    [InlineData("<script src='https://example.com/6000.3/Documentation/ScriptReference/docdata/toc.js'></script>")]
    [InlineData("<script src='docdata/global_toc.js'></script>")]
    public void RejectsExternalOrGlobalToc(string html) =>
        Assert.Throws<InvalidDataException>(() => _extractor.FindTocUrl(html, IndexUrl));

    [Fact]
    public void MissingTocHasClearError()
    {
        var exception = Assert.Throws<InvalidDataException>(() => _extractor.FindTocUrl("<html></html>", IndexUrl));
        Assert.Contains("docdata/toc.js", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractsNestedLinksSkipsCategoriesDeduplicatesAndSorts()
    {
        const string js = "\uFEFF  var toc = {\"link\":\"toc\",\"children\":[{\"link\":\"null\",\"children\":[{\"link\":\"Zed\"},{\"link\":\"Alpha.html\"},{\"link\":\"Zed\"},{\"link\":null}]}]};  ";
        var links = _extractor.ExtractPageLinksFromToc(js).Select(uri => uri.AbsoluteUri).ToArray();
        Assert.Equal(new[]
        {
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Alpha.html",
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Zed.html"
        }, links);
    }

    [Theory]
    [InlineData("const toc = {};")]
    [InlineData("var toc = {broken};")]
    [InlineData("var toc = {\"children\":[]};")]
    [InlineData("var toc = [];")]
    public void InvalidTocInputsAreRejected(string js) =>
        Assert.Throws<InvalidDataException>(() => _extractor.ExtractPageLinksFromToc(js));

    [Theory]
    [InlineData("<h1>  GameObject <em>Class</em> </h1><title>Page title</title>", "GameObject Class")]
    [InlineData("<title>  Vector3 API </title>", "Vector3 API")]
    [InlineData("<p>No title elements</p>", "Quaternion")]
    public void TitleUsesHeadingThenDocumentTitleThenUrlFallback(string html, string expected)
    {
        var url = new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Quaternion.html");
        Assert.Equal(expected, _extractor.GetTitle(html, url));
    }
}
