using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Ingestion;

public sealed class AskRunner
{
    private readonly IRagQueryService _queryService;
    private readonly ILogger<AskRunner> _logger;

    public AskRunner(IRagQueryService queryService, ILogger<AskRunner> logger)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RagAnswer> RunAsync(string question, CancellationToken cancellationToken)
    {
        var userQuestion = new UserQuestion(question);
        cancellationToken.ThrowIfCancellationRequested();
        var answer = await _queryService.AnswerAsync(userQuestion, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Answer status: {AnswerStatus}.", answer.Status);
        if (answer.Language.HasValue)
            _logger.LogInformation("Detected language: {DetectedLanguage}.", answer.Language.Value);
        _logger.LogInformation("Answer: {AnswerText}", answer.Text ?? string.Empty);
        for (var index = 0; index < answer.Citations.Count; index++)
        {
            var citation = answer.Citations[index];
            _logger.LogInformation("Citation {CitationNumber}: {CitationTitle}; section {CitationSection}; URL {CitationUrl}.",
                index + 1, citation.Title, citation.Section ?? "(none)", citation.Url);
        }

        return answer;
    }
}
