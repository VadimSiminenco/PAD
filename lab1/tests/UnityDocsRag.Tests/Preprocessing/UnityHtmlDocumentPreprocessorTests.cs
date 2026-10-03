using System.Security.Cryptography;
using System.Text;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Tests.Preprocessing;

public sealed class UnityHtmlDocumentPreprocessorTests
{
    [Fact]
    public async Task ExtractsTitleSectionsTablesAndCSharpWhileRemovingNoiseAndPreservingMetadata()
    {
        const string html = """
            <header>Header noise</header><aside>Sidebar noise</aside>
            <div id="content-wrap"><div class="content-block"><div class="content"><div class="section">
              <div class="mb20 clear"><h1 class="heading inherit">NavMeshBuildDebugSettings</h1><p>class in<br> UnityEngine.AI</p><p>/</p><p>Implemented in:UnityEngine.AIModule</p><p>Inherits from:Behaviour</p><div class="suggest">Feedback noise</div></div>
              <div class="subsection"><div class="signature"><div class="signature-CS sig-block">public struct NavMeshBuildDebugSettings</div></div></div>
              <div class="subsection"><h3>Description</h3><p>A Unity object.<br>It has <code>flags</code>.</p></div>
              <div class="subsection"><p>Additional details.</p><ol><li>First item</li><li>Second item</li></ol></div>
              <div class="subsection"><h3>Properties</h3><table class="list"><thead><tr><th>Property</th><th>Description</th></tr></thead><tr><td class="lbl"><a>flags</a></td><td class="desc">Description of flags.</td></tr></table></div>
              <div class="subsection"><h3>Inherited Members</h3><div class="subsection"><h3>Properties</h3><table class="list"><tr><td>enabled</td><td>Inherited enabled property.</td></tr></table></div><div class="subsection"><h3>Public Methods</h3><table class="list"><tr><td>GetComponent</td><td>Inherited component method.</td></tr></table></div></div>
              <div class="subsection"><pre>public void Run() { }</pre><form><textarea>bad</textarea></form><script>bad</script><img src="tracking.png" alt="image noise"></div>
            </div></div></div></div><footer>Footer noise</footer>
            """;
        var original = Retrieved(html, new Dictionary<string, string> { ["custom"] = "value" });
        var processed = await new UnityHtmlDocumentPreprocessor().ProcessAsync(original, CancellationToken.None);
        Assert.Contains("# NavMeshBuildDebugSettings", processed.Content);
        Assert.Contains("## Description", processed.Content);
        Assert.Contains("## Properties", processed.Content);
        Assert.Contains("## Inherited Members", processed.Content);
        Assert.Contains("### Properties", processed.Content);
        Assert.Contains("### Public Methods", processed.Content);
        Assert.Contains("## Properties", processed.Content);
        Assert.DoesNotContain("\n## Properties\n", processed.Content[(processed.Content.IndexOf("## Inherited Members", StringComparison.Ordinal))..]);
        Assert.Contains("- enabled — Inherited enabled property.", processed.Content);
        Assert.Contains("- GetComponent — Inherited component method.", processed.Content);
        Assert.Contains("class in UnityEngine.AI", processed.Content);
        Assert.Contains("Implemented in: UnityEngine.AIModule", processed.Content);
        Assert.Contains("Inherits from: Behaviour", processed.Content);
        Assert.DoesNotContain("/", processed.Content);
        Assert.Contains("- flags — Description of flags.", processed.Content);
        Assert.Contains("```csharp\npublic struct NavMeshBuildDebugSettings\n```", processed.Content);
        Assert.Contains("```csharp\npublic void Run() { }\n```", processed.Content);
        Assert.Contains("A Unity object.\nIt has `flags`.", processed.Content);
        Assert.Contains("- First item", processed.Content);
        Assert.Contains("- Second item", processed.Content);
        Assert.Equal(1, Count(processed.Content, "# NavMeshBuildDebugSettings"));
        Assert.Equal(1, Count(processed.Content, "A Unity object."));
        Assert.Equal(1, Count(processed.Content, "public struct NavMeshBuildDebugSettings"));
        Assert.DoesNotContain("Property — Description", processed.Content);
        Assert.DoesNotContain("Sidebar noise", processed.Content);
        Assert.DoesNotContain("Footer noise", processed.Content);
        Assert.DoesNotContain("Feedback noise", processed.Content);
        Assert.DoesNotContain("Header noise", processed.Content);
        Assert.DoesNotContain("image noise", processed.Content);
        Assert.DoesNotContain("bad", processed.Content);
        Assert.Equal(original.DocumentId, processed.DocumentId);
        Assert.Equal(original.CanonicalUrl, processed.CanonicalUrl);
        Assert.Equal(original.RetrievedAt, processed.RetrievedAt);
        Assert.Equal(original.ContentHash, processed.Metadata["sourceContentHash"]);
        Assert.Equal("value", processed.Metadata["custom"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(processed.Content))).ToLowerInvariant(), processed.ContentHash);
    }

    [Fact]
    public async Task MissingMainContainerThrowsClearException() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => new UnityHtmlDocumentPreprocessor().ProcessAsync(Retrieved("<h1>No content</h1>"), CancellationToken.None));

    [Fact]
    public async Task RendersUnitySignaturesAndBrSeparatedCodeExamplesWithoutLosingFormatting()
    {
        const string html = """
            <div id="content-wrap"><div class="section">
              <div class="mb20"><h1>NavMeshAgent.SetDestination</h1></div>
              <div class="signature-CS sig-block"><h2>Declaration</h2>public bool<span>SetDestination</span>(<a href="Vector3.html">Vector3</a> <span>target</span>);</div>
              <pre>using UnityEngine.AI;<br/><br/>public class Example<br/>{<br/>    void Start() { var agent = new <a href="NavMeshAgent.html">NavMeshAgent</a>(); }<br/>}<br/><br/>    void Update()</pre>
            </div></div>
            """;

        var processed = await new UnityHtmlDocumentPreprocessor().ProcessAsync(Retrieved(html), CancellationToken.None);

        Assert.Contains("```csharp\npublic bool SetDestination(Vector3 target);\n```", processed.Content, StringComparison.Ordinal);
        Assert.Contains("```csharp\nusing UnityEngine.AI;\n\npublic class Example", processed.Content, StringComparison.Ordinal);
        Assert.Contains("\n}\n\n    void Update()", processed.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Declarationpublic", processed.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(";public class", processed.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("}    void", processed.Content, StringComparison.Ordinal);

        var codeBlocks = System.Text.RegularExpressions.Regex.Matches(processed.Content, "```csharp\\n(.*?)\\n```",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Equal(2, codeBlocks.Count);
        foreach (System.Text.RegularExpressions.Match codeBlock in codeBlocks)
            Assert.DoesNotContain("`", codeBlock.Groups[1].Value, StringComparison.Ordinal);
        Assert.Equal("2", processed.Metadata["preprocessorVersion"]);
    }

    [Fact]
    public async Task RemovesOnlyCommonLeadingIndentFromFencedCode()
    {
        const string html = """
            <div id="content-wrap"><div class="section">
              <div class="mb20"><h1>API</h1></div>
              <div class="signature-CS sig-block">
                            <h2>Declaration</h2>
                            public bool <span>SetDestination</span>(Vector3 target);
              </div>
              <pre>
                    if (ready)
                    {
                        Run();
                    }
              </pre>
            </div></div>
            """;

        var processed = await new UnityHtmlDocumentPreprocessor().ProcessAsync(Retrieved(html), CancellationToken.None);

        Assert.Contains("```csharp\npublic bool SetDestination(Vector3 target);\n```", processed.Content, StringComparison.Ordinal);
        Assert.Contains("```csharp\nif (ready)\n{\n    Run();\n}\n```", processed.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizationIsDeterministicAndPreservesFencedCodeIndentation()
    {
        const string input = "  alpha   beta  \r\n\r\n\r\n```csharp\r\n  int  x;  \r\n```  ";
        var normalized = TextNormalization.Normalize(input);
        Assert.Equal("alpha beta\n\n\n```csharp\n  int  x;\n```", normalized);
        Assert.Equal(normalized, TextNormalization.Normalize(input));
    }

    private static RetrievedDocument Retrieved(string content, IReadOnlyDictionary<string, string>? metadata = null) =>
        new("doc-1", new Uri("https://docs.unity3d.com/6000.3/Documentation/ScriptReference/GameObject.html"),
            "GameObject", "6000.3", content, "source-hash", DateTimeOffset.UnixEpoch, metadata);

    private static int Count(string value, string fragment) => (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;
}
