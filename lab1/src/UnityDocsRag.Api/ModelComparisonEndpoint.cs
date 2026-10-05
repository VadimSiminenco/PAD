using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Api;

public sealed record ModelComparisonSnapshot(
    DateTimeOffset CompletedAt,
    string Status,
    IReadOnlyList<ModelComparisonSnapshotModel> Models,
    double? FasterRatio);

public sealed record ModelComparisonSnapshotModel(string Model, string Status, double? GenerationSeconds);

public interface IModelComparisonSnapshotStore
{
    Task SaveAsync(ModelComparisonSnapshot snapshot, CancellationToken cancellationToken);
    Task<ModelComparisonSnapshot?> ReadAsync(CancellationToken cancellationToken);
}

public sealed class FileModelComparisonSnapshotStore(string path) : IModelComparisonSnapshotStore
{
    public async Task SaveAsync(ModelComparisonSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Snapshot directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<ModelComparisonSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<ModelComparisonSnapshot>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public static class ModelComparisonEndpoint
{
    private const string SafeFailure = "Не удалось выполнить сравнение моделей. Проверьте локальные службы и повторите запрос.";

    public static async Task<IResult> HandleAsync(AskRequest? request, ModelComparisonService service,
        IModelComparisonSnapshotStore store, ILogger<AskApiEndpoint> logger, CancellationToken cancellationToken)
    {
        var question = request?.Question;
        if (string.IsNullOrWhiteSpace(question)) return Results.BadRequest(new AskErrorResponse("Введите вопрос."));
        if (question.Length > AskApiEndpoint.MaxQuestionLength)
            return Results.BadRequest(new AskErrorResponse($"Вопрос не должен превышать {AskApiEndpoint.MaxQuestionLength} символов."));

        using var activity = LangfuseTelemetry.ActivitySource.StartActivity("api.model_comparison", ActivityKind.Server);
        try
        {
            var result = await service.CompareAsync(new UserQuestion(question), cancellationToken).ConfigureAwait(false);
            activity?.SetTag("rag.status", result.Status);
            if (result.Language is not null) activity?.SetTag("rag.language", result.Language);
            if (result.IsComparison)
            {
                var snapshot = new ModelComparisonSnapshot(DateTimeOffset.UtcNow, result.Status,
                    result.Models.Select(model => new ModelComparisonSnapshotModel(model.Model, model.Status, model.GenerationSeconds)).ToArray(),
                    FasterRatio(result.Models));
                try { await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning("Could not persist model comparison snapshot ({ExceptionType})", exception.GetType().Name);
                }
            }
            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            logger.LogError("Model comparison failed ({ExceptionType})", exception.GetType().Name);
            return Results.Json(new AskErrorResponse(SafeFailure), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public static async Task<IResult> GetLastAsync(IModelComparisonSnapshotStore store, CancellationToken cancellationToken)
    {
        try { return Results.Ok(await store.ReadAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
        catch { return Results.Json(new AskErrorResponse("Не удалось прочитать сохранённый замер."), statusCode: 500); }
    }

    private static double? FasterRatio(IReadOnlyList<ModelComparisonAnswer> models)
    {
        if (models.Count != 2 || models.Any(model => model.GenerationSeconds is null || model.Status is "Error" or "Failed")) return null;
        var first = models[0].GenerationSeconds!.Value;
        var second = models[1].GenerationSeconds!.Value;
        if (first == 0 || second == 0) return null;
        return Math.Max(first, second) / Math.Min(first, second);
    }
}
