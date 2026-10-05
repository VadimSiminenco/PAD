using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnityDocsRag.Evaluation;

internal sealed record AskEvaluationCheckpoint(
    int Version,
    string DatasetSha256,
    string ConfigSha256,
    string OutputPathSha256,
    IReadOnlyList<string> QuestionIds,
    IReadOnlyList<AskQuestionEvaluation> CompletedQuestions);

internal sealed class AskEvaluationCheckpointStore
{
    private const int CheckpointVersion = 1;
    private readonly string _path;
    private readonly string _datasetHash;
    private readonly string _configHash;
    private readonly string _outputPathHash;
    private readonly string[] _questionIds;

    private AskEvaluationCheckpointStore(string outputPath, string datasetHash, string configHash,
        string[] questionIds)
    {
        _path = CheckpointPath(outputPath);
        _datasetHash = datasetHash;
        _configHash = configHash;
        var normalizedOutputPath = Path.GetFullPath(outputPath);
        if (OperatingSystem.IsWindows()) normalizedOutputPath = normalizedOutputPath.ToUpperInvariant();
        _outputPathHash = HashBytes(Encoding.UTF8.GetBytes(normalizedOutputPath));
        _questionIds = questionIds;
    }

    public static string CheckpointPath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return Path.GetFullPath(outputPath) + ".checkpoint.json";
    }

    public static async Task<AskEvaluationCheckpointStore> CreateAsync(EvaluationDataset dataset,
        string datasetPath, string configPath, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var datasetHash = await HashFileAsync(datasetPath, cancellationToken).ConfigureAwait(false);
        var configHash = await HashFileAsync(configPath, cancellationToken).ConfigureAwait(false);
        return new AskEvaluationCheckpointStore(outputPath, datasetHash, configHash,
            dataset.Questions.Select(question => question.Id).ToArray());
    }

    public async Task<IReadOnlyList<AskQuestionEvaluation>> LoadCompletedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return Array.Empty<AskQuestionEvaluation>();

        AskEvaluationCheckpoint checkpoint;
        try
        {
            await using var stream = File.OpenRead(_path);
            checkpoint = await JsonSerializer.DeserializeAsync<AskEvaluationCheckpoint>(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Evaluation checkpoint is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Evaluation checkpoint is not valid JSON.", exception);
        }

        if (checkpoint.Version != CheckpointVersion ||
            checkpoint.DatasetSha256 != _datasetHash ||
            checkpoint.ConfigSha256 != _configHash ||
            checkpoint.OutputPathSha256 != _outputPathHash ||
            checkpoint.QuestionIds is null ||
            !checkpoint.QuestionIds.SequenceEqual(_questionIds, StringComparer.Ordinal) ||
            checkpoint.CompletedQuestions is null ||
            checkpoint.CompletedQuestions.Count > _questionIds.Length)
        {
            throw new InvalidDataException("Evaluation checkpoint does not match the dataset, config, output path, or question IDs.");
        }

        for (var index = 0; index < checkpoint.CompletedQuestions.Count; index++)
        {
            if (checkpoint.CompletedQuestions[index] is null ||
                !string.Equals(checkpoint.CompletedQuestions[index].Id, _questionIds[index], StringComparison.Ordinal))
            {
                throw new InvalidDataException("Evaluation checkpoint has a non-prefix question ID sequence.");
            }
        }

        return checkpoint.CompletedQuestions;
    }

    public async Task SaveAsync(IReadOnlyList<AskQuestionEvaluation> completedQuestions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completedQuestions);
        if (completedQuestions.Count > _questionIds.Length ||
            completedQuestions.Where((result, index) =>
                !string.Equals(result.Id, _questionIds[index], StringComparison.Ordinal)).Any())
        {
            throw new InvalidDataException("Completed questions must be a prefix of the dataset ID sequence.");
        }

        var checkpoint = new AskEvaluationCheckpoint(CheckpointVersion, _datasetHash, _configHash,
            _outputPathHash, _questionIds, completedQuestions.ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The temporary file is in the same directory, so the rename replaces one complete JSON snapshot.
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
