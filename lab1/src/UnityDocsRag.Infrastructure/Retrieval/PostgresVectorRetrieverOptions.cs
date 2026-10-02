namespace UnityDocsRag.Infrastructure.Retrieval;

public sealed class PostgresVectorRetrieverOptions
{
    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        if (CommandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), "Command timeout must be positive.");
    }
}
