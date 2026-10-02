using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Documents;
using UnityDocsRag.Infrastructure.Preprocessing;

namespace UnityDocsRag.Ingestion;

public sealed class ProcessingRunner
{
    private readonly IDocumentSource _source;
    private readonly IDocumentPreprocessor _preprocessor;
    private readonly IDocumentChunker _chunker;
    private readonly FileProcessingArtifactStore _artifactStore;
    private readonly ILogger<ProcessingRunner> _logger;

    public ProcessingRunner(IDocumentSource source, IDocumentPreprocessor preprocessor, IDocumentChunker chunker,
        FileProcessingArtifactStore artifactStore, ILogger<ProcessingRunner> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProcessingRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        var retrieved = new List<RetrievedDocument>();
        await foreach (var document in _source.GetDocumentsAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retrieved.Add(document);
        }
        if (retrieved.Count == 0) throw new InvalidDataException("Local cache contains no documents to process.");
        var duplicateDocId = retrieved.GroupBy(document => document.DocumentId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateDocId is not null) throw new InvalidDataException($"Document source returned duplicate DocumentId '{duplicateDocId.Key}'.");

        var processed = new List<ProcessedDocument>(retrieved.Count);
        var chunks = new List<DocumentChunk>();
        foreach (var document in retrieved.OrderBy(document => document.DocumentId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processedDocument = await _preprocessor.ProcessAsync(document, cancellationToken).ConfigureAwait(false);
            processed.Add(processedDocument);
            chunks.AddRange(await _chunker.ChunkAsync(processedDocument, cancellationToken).ConfigureAwait(false));
        }
        var duplicateProcessed = processed.GroupBy(document => document.DocumentId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateProcessed is not null) throw new InvalidDataException($"Processing produced duplicate DocumentId '{duplicateProcessed.Key}'.");
        if (chunks.Count == 0) throw new InvalidDataException("Processing produced no document chunks.");
        var duplicateChunk = chunks.GroupBy(chunk => chunk.ChunkId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateChunk is not null) throw new InvalidDataException($"Processing produced duplicate ChunkId '{duplicateChunk.Key}'.");
        var documentIds = processed.Select(document => document.DocumentId).ToHashSet(StringComparer.Ordinal);
        var dangling = chunks.FirstOrDefault(chunk => !documentIds.Contains(chunk.DocumentId));
        if (dangling is not null) throw new InvalidDataException($"Chunk '{dangling.ChunkId}' references unknown DocumentId '{dangling.DocumentId}'.");

        var sortedDocuments = processed.OrderBy(document => document.DocumentId, StringComparer.Ordinal).ToArray();
        var sortedChunks = chunks.OrderBy(chunk => chunk.DocumentId, StringComparer.Ordinal).ThenBy(chunk => chunk.Ordinal).ToArray();
        await _artifactStore.SaveAsync(sortedDocuments, sortedChunks, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Processing complete: documents {DocumentCount}, chunks {ChunkCount}.", sortedDocuments.Length, sortedChunks.Length);
        return new ProcessingRunSummary(sortedDocuments.Length, sortedChunks.Length);
    }
}

public sealed record ProcessingRunSummary(int DocumentCount, int ChunkCount);
