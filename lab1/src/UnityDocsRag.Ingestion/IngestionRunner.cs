using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Infrastructure.Documentation;

namespace UnityDocsRag.Ingestion;

public sealed record IngestionSummary(int Retrieved, int New, int Updated, int Unchanged);

public sealed class IngestionRunner
{
    private readonly IDocumentSource _documentSource;
    private readonly FileDocumentCache _cache;
    private readonly ILogger<IngestionRunner> _logger;

    public IngestionRunner(
        IDocumentSource documentSource,
        FileDocumentCache cache,
        ILogger<IngestionRunner> logger)
    {
        _documentSource = documentSource ?? throw new ArgumentNullException(nameof(documentSource));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IngestionSummary> RunAsync(CancellationToken cancellationToken)
    {
        var retrieved = 0;
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        await foreach (var document in _documentSource.GetDocumentsAsync(cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retrieved++;
            var result = await _cache.SaveAsync(document, cancellationToken).ConfigureAwait(false);
            switch (result.ChangeKind)
            {
                case DocumentCacheChangeKind.New:
                    added++;
                    break;
                case DocumentCacheChangeKind.Updated:
                    updated++;
                    break;
                case DocumentCacheChangeKind.Unchanged:
                    unchanged++;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown cache result '{result.ChangeKind}'.");
            }
        }

        if (retrieved == 0)
        {
            throw new InvalidOperationException("Unity documentation source returned no pages; ingestion did not run.");
        }

        var summary = new IngestionSummary(retrieved, added, updated, unchanged);
        _logger.LogInformation(
            "Ingestion complete: retrieved {Retrieved}, new {New}, updated {Updated}, unchanged {Unchanged}",
            summary.Retrieved,
            summary.New,
            summary.Updated,
            summary.Unchanged);
        return summary;
    }
}
