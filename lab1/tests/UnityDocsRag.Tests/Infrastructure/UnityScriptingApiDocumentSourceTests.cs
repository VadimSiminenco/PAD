using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Documentation;

namespace UnityDocsRag.Tests.Infrastructure;

public sealed class UnityScriptingApiDocumentSourceTests
{
    private const string IndexUrl = UnityDocumentationSourceOptions.DefaultBaseUrl;
    private const string TocUrl = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/docdata/toc.js";
    private const string PageA = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/A.html";
    private const string PageB = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/B.html";
    private const string NavMeshAgent = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.html";
    private const string AgentType = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent-agentType.html";
    private const string SetDestination = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AI.NavMeshAgent.SetDestination.html";
    private const string RecursiveMember = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Recursive.html";
    private const string IndexHtml = "<script src='docdata/toc.js'></script>";
    private const string Toc = "var toc = {\"children\":[{\"link\":\"B\"},{\"link\":\"A\"}]};";

    [Fact]
    public async Task SourceHonorsMaxPagesCreatesDocumentsAndRequestsIndexTocThenPages()
    {
        var handler = ConfiguredHandler();
        handler.Add(PageA, HtmlResponse("<h1>Alpha</h1><p>Alpha docs</p>", withMetadata: true));
        handler.Add(PageB, HtmlResponse("<h1>Beta</h1><p>Beta docs</p>"));
        using var httpClient = new HttpClient(handler);
        var source = CreateSource(httpClient, maxPages: 1);
        var first = await CollectAsync(source.GetDocumentsAsync(CancellationToken.None));
        var doc = Assert.Single(first);
        Assert.Equal(PageA, doc.CanonicalUrl.AbsoluteUri);
        Assert.Equal("Alpha", doc.Title);
        Assert.Equal("6000.3", doc.UnityVersion);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Content))).ToLowerInvariant(), doc.ContentHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PageA))).ToLowerInvariant(), doc.DocumentId);
        Assert.Equal("unity-scripting-api", doc.Metadata["source"]);
        Assert.Equal("text/html; charset=utf-8", doc.Metadata["contentType"]);
        Assert.Equal("\"test-etag\"", doc.Metadata["etag"]);
        Assert.Equal(new[] { IndexUrl, TocUrl, PageA }, handler.Requests);
    }

    [Fact]
    public async Task FailureOfOnePageDoesNotCancelRemainingPages()
    {
        var handler = ConfiguredHandler();
        handler.Add(PageA, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        handler.Add(PageB, HtmlResponse("<h1>Beta</h1>"));
        using var client = new HttpClient(handler);
        var docs = await CollectAsync(CreateSource(client).GetDocumentsAsync(CancellationToken.None));
        Assert.Equal(PageB, Assert.Single(docs).CanonicalUrl.AbsoluteUri);
    }

    [Fact]
    public async Task IndexFailureIsFatal()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(IndexUrl, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(CreateSource(client).GetDocumentsAsync(CancellationToken.None)));
        Assert.Contains("required Unity Scripting API index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TocDownloadFailureIsFatal()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(IndexUrl, HtmlResponse(IndexHtml));
        handler.Add(TocUrl, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(CreateSource(client).GetDocumentsAsync(CancellationToken.None)));
        Assert.Contains("required Unity Scripting API TOC", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedTocIsFatal()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(IndexUrl, HtmlResponse(IndexHtml));
        handler.Add(TocUrl, HtmlResponse("not javascript"));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(CreateSource(client).GetDocumentsAsync(CancellationToken.None)));
        Assert.Contains("process the required Unity Scripting API TOC", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler((_, token) =>
        {
            cts.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await CollectAsync(CreateSource(client).GetDocumentsAsync(cts.Token)));
    }

    [Fact]
    public async Task SelectsExactSeedLoadsItFirstThenSortedDeduplicatedDirectMembersOnly()
    {
        var handler = SeededHandler();
        handler.Add(NavMeshAgent, HtmlResponse("""
            <h1>NavMeshAgent</h1><div class="section">
              <div class="subsection"><h2>Properties</h2><table class="list"><tr><td><a href="AI.NavMeshAgent.SetDestination.html">method</a></td><td><a href="AI.NavMeshAgent-agentType.html">property</a></td><td><a href="AI.NavMeshAgent-agentType.html">duplicate</a></td></tr></table></div>
              <div class="subsection"><h2>Inherited Members</h2><table class="list"><tr><td><a href="Inherited.html">inherited</a></td></tr></table></div>
            </div>
            """));
        handler.Add(AgentType, HtmlResponse("<h1>agentType</h1><div class='section'><div class='subsection'><h2>Methods</h2><table class='list'><a href='Recursive.html'>recursive</a></table></div></div>"));
        handler.Add(SetDestination, HtmlResponse("<h1>SetDestination</h1>"));
        using var client = new HttpClient(handler);

        var documents = await CollectAsync(CreateSource(client, maxPages: 3,
            seedPages: ["AI.NavMeshAgent.html"], includeMemberPages: true).GetDocumentsAsync(CancellationToken.None));

        Assert.Equal(new[] { "NavMeshAgent", "agentType", "SetDestination" }, documents.Select(document => document.Title));
        Assert.Equal(new[] { IndexUrl, TocUrl, NavMeshAgent, AgentType, SetDestination }, handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.Contains("Inherited", StringComparison.Ordinal));
        Assert.DoesNotContain(RecursiveMember, handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.EndsWith("Decoy.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingConfiguredSeedIsAFatalClearError()
    {
        var handler = SeededHandler();
        using var client = new HttpClient(handler);
        var source = CreateSource(client, seedPages: ["AI.MissingType.html"], includeMemberPages: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(source.GetDocumentsAsync(CancellationToken.None)));

        Assert.Contains("seed page(s) were not found in the TOC", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AI.MissingType.html", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { IndexUrl, TocUrl }, handler.Requests);
    }

    [Fact]
    public async Task MaxPagesLimitsCombinedSeedAndMemberRequests()
    {
        var handler = SeededHandler();
        handler.Add(NavMeshAgent, HtmlResponse("<div class='section'><div class='subsection'><h2>Methods</h2><table class='list'><tr><td><a href='AI.NavMeshAgent.SetDestination.html'>B</a></td></tr><tr><td><a href='AI.NavMeshAgent-agentType.html'>A</a></td></tr></table></div></div>"));
        handler.Add(AgentType, HtmlResponse("<h1>agentType</h1>"));
        using var client = new HttpClient(handler);

        var documents = await CollectAsync(CreateSource(client, maxPages: 2,
            seedPages: ["AI.NavMeshAgent.html"], includeMemberPages: true).GetDocumentsAsync(CancellationToken.None));

        Assert.Equal(new[] { NavMeshAgent, AgentType }, handler.Requests.Skip(2).ToArray());
        Assert.Equal(2, documents.Count);
    }

    [Fact]
    public async Task DisablingMemberPagesLoadsOnlyConfiguredSeed()
    {
        var handler = SeededHandler();
        handler.Add(NavMeshAgent, HtmlResponse("<div class='section'><div class='subsection'><h2>Methods</h2><table class='list'><tr><td><a href='AI.NavMeshAgent.SetDestination.html'>member</a></td></tr></table></div></div>"));
        using var client = new HttpClient(handler);

        var documents = await CollectAsync(CreateSource(client, seedPages: ["AI.NavMeshAgent.html"], includeMemberPages: false)
            .GetDocumentsAsync(CancellationToken.None));

        Assert.Single(documents);
        Assert.Equal(new[] { IndexUrl, TocUrl, NavMeshAgent }, handler.Requests);
    }

    [Fact]
    public async Task FailureOfOneMemberDoesNotCancelOtherMemberDownloads()
    {
        var handler = SeededHandler();
        handler.Add(NavMeshAgent, HtmlResponse("<div class='section'><div class='subsection'><h2>Methods</h2><table class='list'><tr><td><a href='AI.NavMeshAgent-agentType.html'>A</a></td></tr><tr><td><a href='AI.NavMeshAgent.SetDestination.html'>B</a></td></tr></table></div></div>"));
        handler.Add(AgentType, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        handler.Add(SetDestination, HtmlResponse("<h1>SetDestination</h1>"));
        using var client = new HttpClient(handler);

        var documents = await CollectAsync(CreateSource(client, maxPages: 3,
            seedPages: ["AI.NavMeshAgent.html"], includeMemberPages: true).GetDocumentsAsync(CancellationToken.None));

        Assert.Equal(new[] { "AI.NavMeshAgent", "SetDestination" }, documents.Select(document => document.Title));
        Assert.Equal(new[] { NavMeshAgent, AgentType, SetDestination }, handler.Requests.Skip(2));
    }

    [Fact]
    public async Task RequestDelayIsAppliedBetweenSeedAndMemberPageRequests()
    {
        var handler = SeededHandler();
        handler.Add(NavMeshAgent, HtmlResponse("<div class='section'><div class='subsection'><h2>Methods</h2><table class='list'><tr><td><a href='AI.NavMeshAgent.SetDestination.html'>member</a></td></tr></table></div></div>"));
        handler.Add(SetDestination, HtmlResponse("<h1>SetDestination</h1>"));
        using var client = new HttpClient(handler);

        await CollectAsync(CreateSource(client, maxPages: 2, seedPages: ["AI.NavMeshAgent.html"],
            includeMemberPages: true, delayMilliseconds: 50).GetDocumentsAsync(CancellationToken.None));

        Assert.True(handler.RequestTimes[3] - handler.RequestTimes[2] >= TimeSpan.FromMilliseconds(35));
    }

    [Fact]
    public void OptionsValidateSeedNamesAndAllowTheExpandedPageLimit()
    {
        Assert.Throws<ArgumentException>(() => new UnityDocumentationSourceOptions { SeedPages = ["../Type.html"] }.Validate());
        Assert.Throws<ArgumentException>(() => new UnityDocumentationSourceOptions { SeedPages = ["Type.html", "Type.html"] }.Validate());
        Assert.ThrowsAny<ArgumentException>(() => new UnityDocumentationSourceOptions { SeedPages = ["Type.html"], MaxPages = 0 }.Validate());
        new UnityDocumentationSourceOptions { MaxPages = 500 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnityDocumentationSourceOptions { MaxPages = 501 }.Validate());
    }

    private static FakeHttpMessageHandler ConfiguredHandler()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(IndexUrl, HtmlResponse(IndexHtml));
        handler.Add(TocUrl, HtmlResponse(Toc));
        return handler;
    }

    private static FakeHttpMessageHandler SeededHandler()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(IndexUrl, HtmlResponse(IndexHtml));
        handler.Add(TocUrl, HtmlResponse("var toc = {\"children\":[{\"link\":\"AI.NavMeshAgentExtra\"},{\"link\":\"AI.NavMeshAgent\"},{\"link\":\"Decoy\"}]};"));
        return handler;
    }

    private static UnityScriptingApiDocumentSource CreateSource(
        HttpClient client,
        int maxPages = 5,
        IReadOnlyList<string>? seedPages = null,
        bool includeMemberPages = false,
        int delayMilliseconds = 0) => new(
        client,
        new UnityDocumentationSourceOptions
        {
            MaxPages = maxPages,
            SeedPages = seedPages ?? Array.Empty<string>(),
            IncludeMemberPages = includeMemberPages,
            RequestDelayMilliseconds = delayMilliseconds
        },
        new UnityDocumentationLinkExtractor(),
        NullLogger<UnityScriptingApiDocumentSource>.Instance);

    private static HttpResponseMessage HtmlResponse(string text, bool withMetadata = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
        if (withMetadata)
        {
            response.Headers.ETag = new EntityTagHeaderValue("\"test-etag\"");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        }
        return response;
    }

    private static async Task<List<RetrievedDocument>> CollectAsync(IAsyncEnumerable<RetrievedDocument> source)
    {
        var docs = new List<RetrievedDocument>();
        await foreach (var doc in source) docs.Add(doc);
        return docs;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> _responses = new(StringComparer.Ordinal);
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _custom;
        public FakeHttpMessageHandler() { }
        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> custom) => _custom = custom;
        public List<string> Requests { get; } = new();
        public List<DateTimeOffset> RequestTimes { get; } = new();

        public void Add(string url, HttpResponseMessage response)
        {
            var status = response.StatusCode;
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var contentType = response.Content.Headers.ContentType?.ToString();
            var etag = response.Headers.ETag;
            var modified = response.Content.Headers.LastModified;
            _responses.Add(url, (_, _) =>
            {
                var clone = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
                if (contentType is not null) clone.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                clone.Headers.ETag = etag;
                clone.Content.Headers.LastModified = modified;
                return clone;
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            RequestTimes.Add(DateTimeOffset.UtcNow);
            if (_custom is not null) return _custom(request, cancellationToken);
            return Task.FromResult(_responses.TryGetValue(request.RequestUri.AbsoluteUri, out var factory)
                ? factory(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
