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

    [Fact]
    public void ExtractsOnlyDirectPropertyAndMethodLinksIncludingSetDestination()
    {
        const string html = """
            <div class="section">
              <div class="subsection"><h2>Properties</h2><table class="list"><tr><td><a href="AI.NavMeshAgent-agentType.html">agentType</a></td></tr></table></div>
              <div class="subsection"><h2>Methods</h2><table class="list"><tr><td><a href="AI.NavMeshAgent.SetDestination.html">SetDestination</a></td></tr></table></div>
            </div>
            """;

        var links = _extractor.ExtractDirectMemberPageLinks(html, new Uri(IndexUrl.AbsoluteUri + "AI.NavMeshAgent.html"));

        Assert.Equal(new[]
        {
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent-agentType.html",
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.SetDestination.html"
        }, links.Select(link => link.AbsoluteUri));
    }

    [Fact]
    public void ExcludesInheritedMembersAndLinksOutsideMainSection()
    {
        const string html = """
            <header><div class="subsection"><table class="list"><tr><td><a href="Header.html">Header</a></td></tr></table></div></header>
            <aside><a href="Sidebar.html">Sidebar</a></aside>
            <div class="section">
              <div class="subsection"><h2>Methods</h2><table class="list"><tr><td><a href="OwnMethod.html">Own method</a></td></tr></table></div>
              <div class="subsection"><h2>Inherited Members</h2><div class="subsection"><table class="list"><tr><td><a href="Inherited.html">Inherited</a></td></tr></table></div></div>
            </div>
            <footer><a href="Footer.html">Footer</a></footer>
            """;

        var links = _extractor.ExtractDirectMemberPageLinks(html, new Uri(IndexUrl.AbsoluteUri + "AI.NavMeshAgent.html"));

        Assert.Equal(new[] { "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/OwnMethod.html" },
            links.Select(link => link.AbsoluteUri));
    }

    [Fact]
    public void RejectsManualExternalQueryFragmentAndSourcePageLinks()
    {
        const string html = """
            <div class="section"><div class="subsection"><h2>Methods</h2><table class="list">
              <tr><td><a href="https://example.com/6000.3/Documentation/ScriptReference/External.html">external</a></td></tr>
              <tr><td><a href="/6000.3/Documentation/Manual/ManualPage.html">manual</a></td></tr>
              <tr><td><a href="https://docs.unity3d.com/6000.3/Documentation/Packages/com.unity.ai/PackagePage.html">package</a></td></tr>
              <tr><td><a href="Method.html?x=1">query</a></td></tr>
              <tr><td><a href="Method.html#part">fragment</a></td></tr>
              <tr><td><a href="AI.NavMeshAgent.html">self</a></td></tr>
              <tr><td><a href="//docs.unity3d.com:444/6000.3/Documentation/ScriptReference/Port.html">port</a></td></tr>
            </table></div></div>
            """;

        var links = _extractor.ExtractDirectMemberPageLinks(html, new Uri(IndexUrl.AbsoluteUri + "AI.NavMeshAgent.html"));

        Assert.Empty(links);
    }

    [Fact]
    public void DeduplicatesAndSortsByCanonicalAbsoluteUrl()
    {
        const string html = """
            <div class="section"><div class="subsection"><h2>Methods</h2><table class="list">
              <tr><td><a href="Zed.html">zed</a></td></tr>
              <tr><td><a href="Alpha.html">alpha</a></td></tr>
              <tr><td><a href="./Alpha.html">duplicate</a></td></tr>
            </table></div></div>
            """;

        var links = _extractor.ExtractDirectMemberPageLinks(html, new Uri(IndexUrl.AbsoluteUri + "AI.NavMeshAgent.html"));

        Assert.Equal(new[]
        {
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Alpha.html",
            "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Zed.html"
        }, links.Select(link => link.AbsoluteUri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not valid markup {{{")]
    [InlineData("<div class='section'><table class='list'><a href='Accidental.html'>orphan</a>")]
    public void EmptyOrMalformedHtmlDoesNotProduceAccidentalLinks(string html) =>
        Assert.Empty(_extractor.ExtractDirectMemberPageLinks(html, IndexUrl));
}
