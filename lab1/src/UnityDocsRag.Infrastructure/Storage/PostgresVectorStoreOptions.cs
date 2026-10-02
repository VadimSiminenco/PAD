using Npgsql;
using Pgvector.Npgsql;

namespace UnityDocsRag.Infrastructure.Storage;

public sealed class PostgresVectorStoreOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new ArgumentException("PostgreSQL connection string must not be empty.", nameof(ConnectionString));
        if (CommandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), "Command timeout must be positive.");
        NpgsqlConnectionStringBuilder parsed;
        try { parsed = new NpgsqlConnectionStringBuilder(ConnectionString); }
        catch (ArgumentException)
        {
            throw new ArgumentException("PostgreSQL connection string is invalid.", nameof(ConnectionString));
        }
        if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database) || string.IsNullOrWhiteSpace(parsed.Username))
            throw new ArgumentException("PostgreSQL connection string must specify Host, Database, and Username.", nameof(ConnectionString));
    }

    /// <summary>Creates a vector-enabled data source. The caller owns and must dispose the returned data source.</summary>
    public NpgsqlDataSource CreateDataSource()
    {
        Validate();
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseVector();
        return builder.Build();
    }
}
