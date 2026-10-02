using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;

namespace UnityDocsRag.Infrastructure.Documentation;

public sealed class UnityScriptingApiDocumentSource : IDocumentSource
{
    private readonly HttpClient _httpClient;
    private readonly UnityDocumentationSourceOptions _options;
    private readonly UnityDocumentationLinkExtractor _linkExtractor;
    private readonly ILogger<UnityScriptingApiDocumentSource> _logger;

    public UnityScriptingApiDocumentSource(
        HttpClient httpClient,
        UnityDocumentationSourceOptions options,
        UnityDocumentationLinkExtractor linkExtractor,
        ILogger<UnityScriptingApiDocumentSource> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _linkExtractor = linkExtractor ?? throw new ArgumentNullException(nameof(linkExtractor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async IAsyncEnumerable<RetrievedDocument> GetDocumentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var indexUrl = new Uri(_options.BaseUrl, UriKind.Absolute);
        HtmlDownload index;
        try
        {
            index = await DownloadAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not download the required Unity Scripting API index at '{indexUrl}': {exception.Message}",
                exception);
        }

        Uri tocUrl;
        try
        {
            tocUrl = _linkExtractor.FindTocUrl(index.Content, indexUrl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not locate the required Unity Scripting API TOC in '{indexUrl}': {exception.Message}", exception);
        }

        string tocContent;
        try
        {
            tocContent = (await DownloadAsync(tocUrl, cancellationToken).ConfigureAwait(false)).Content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not download the required Unity Scripting API TOC at '{tocUrl}': {exception.Message}", exception);
        }

        Uri[] pageUrls;
        try
        {
            pageUrls = _linkExtractor.ExtractPageLinksFromToc(tocContent).Take(_options.MaxPages).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not process the required Unity Scripting API TOC at '{tocUrl}': {exception.Message}", exception);
        }

        for (var indexInPages = 0; indexInPages < pageUrls.Length; indexInPages++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (indexInPages > 0 && _options.RequestDelayMilliseconds > 0)
            {
                await Task.Delay(_options.RequestDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            RetrievedDocument? document;
            var pageUrl = pageUrls[indexInPages];
            try
            {
                var page = await DownloadAsync(pageUrl, cancellationToken).ConfigureAwait(false);
                document = CreateDocument(pageUrl, page);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Skipping Unity documentation page {PageUrl} after download failure", pageUrl);
                continue;
            }

            yield return document;
        }
    }

    private async Task<HtmlDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HtmlDownload(
            content,
            response.Content.Headers.ContentType?.ToString() ?? "text/html",
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToUniversalTime().ToString("O"));
    }

    private RetrievedDocument CreateDocument(Uri url, HtmlDownload page)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.Content))).ToLowerInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri))).ToLowerInvariant();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "unity-scripting-api",
            ["contentType"] = page.ContentType
        };
        if (page.ETag is not null)
        {
            metadata["etag"] = page.ETag;
        }

        if (page.LastModified is not null)
        {
            metadata["lastModified"] = page.LastModified;
        }

        return new RetrievedDocument(
            id,
            url,
            _linkExtractor.GetTitle(page.Content, url),
            _options.UnityVersion,
            page.Content,
            hash,
            DateTimeOffset.UtcNow,
            metadata);
    }

    private sealed record HtmlDownload(string Content, string ContentType, string? ETag, string? LastModified);
}
