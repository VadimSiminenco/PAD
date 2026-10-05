using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Abstractions;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Api;

public sealed record AskRequest(string? Question);

public sealed record AskResponse(
    string Status,
    string? Language,
    string? Text,
    IReadOnlyList<AskCitationResponse> Citations);

public sealed record AskCitationResponse(string Title, string? Section, string Url);

public sealed record AskErrorResponse(string Message);

public sealed class AskApiEndpoint
{
    public const int MaxQuestionLength = 2000;
    private const string SafeFailureMessage = "Не удалось получить ответ. Проверьте локальные службы и повторите запрос.";

    public static async Task<IResult> HandleAsync(
        AskRequest? request,
        IRagQueryService queryService,
        ILogger<AskApiEndpoint> logger,
        CancellationToken cancellationToken)
    {
        using var activity = LangfuseTelemetry.ActivitySource.StartActivity(
            "api.ask", ActivityKind.Server, parentContext: default);
        var started = activity is null ? 0L : Stopwatch.GetTimestamp();
        var question = request?.Question;
        if (string.IsNullOrWhiteSpace(question))
        {
            activity?.SetTag("rag.status", "InvalidQuestion");
            return Results.BadRequest(new AskErrorResponse("Введите вопрос."));
        }
        if (question.Length > MaxQuestionLength)
        {
            activity?.SetTag("rag.status", "QuestionTooLong");
            return Results.BadRequest(new AskErrorResponse($"Вопрос не должен превышать {MaxQuestionLength} символов."));
        }

        try
        {
            var answer = await queryService.AnswerAsync(new UserQuestion(question), cancellationToken)
                .ConfigureAwait(false);
            activity?.SetTag("rag.status", answer.Status.ToString());
            if (answer.Language.HasValue) activity?.SetTag("rag.language", answer.Language.Value.ToString());
            return Results.Ok(new AskResponse(
                answer.Status.ToString(),
                answer.Language?.ToString(),
                answer.Text,
                answer.Citations.Select(citation => new AskCitationResponse(
                    citation.Title, citation.Section, citation.Url.AbsoluteUri)).ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("rag.status", "Cancelled");
            return Results.StatusCode(499);
        }
        catch (Exception exception)
        {
            activity?.SetTag("rag.status", "Error");
            if (activity is not null) activity.SetStatus(ActivityStatusCode.Error);
            logger.LogError("Ask request failed ({ExceptionType})", exception.GetType().Name);
            return Results.Json(new AskErrorResponse(SafeFailureMessage), statusCode: StatusCodes.Status500InternalServerError);
        }
        finally
        {
            if (activity is not null)
                activity.SetTag("rag.duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
