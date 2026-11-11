using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Ora2PgVerify.Cli;

/// <summary>
/// Resolves connection strings. Environment variables are the recommended channel: command-line arguments are
/// visible to every user of the machine through the process list. Nothing here is ever logged or written to a report.
/// </summary>
internal static class Connections
{
    public const string OracleEnv = "ORA2PGV_ORACLE";
    public const string PostgresEnv = "ORA2PGV_POSTGRES";
    public const string OracleOwnerEnv = "ORA2PGV_ORACLE_OWNER";
    public const string PostgresOwnerEnv = "ORA2PGV_POSTGRES_OWNER";

    public static string Resolve(string? value, string env, string what) =>
        !string.IsNullOrWhiteSpace(value) ? value
        : Environment.GetEnvironmentVariable(env) is { Length: > 0 } fromEnv ? fromEnv
        : throw new UsageException($"No {what} connection string: set {env} (preferred) or pass the option.");

    /// <summary>Host/service only, for reports and logs.</summary>
    public static string OracleEndpoint(string connectionString)
    {
        try
        {
            return new OracleConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return "oracle";
        }
    }

    public static string PostgresEndpoint(string connectionString)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{b.Host}:{b.Port}/{b.Database}";
        }
        catch (ArgumentException)
        {
            return "postgres";
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);
