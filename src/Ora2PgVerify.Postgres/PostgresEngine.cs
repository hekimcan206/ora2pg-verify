using System.Data;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Sql;

namespace Ora2PgVerify.Postgres;

/// <summary>Npgsql adapter for the generic row source.</summary>
public sealed class PostgresEngine(NpgsqlDataSource dataSource) : EngineAdapter
{
    public override SqlDialect Dialect => PostgresDialect.Instance;

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // Deterministic sessions: UTC, so nothing in the canonical encoding depends on the server's TimeZone setting.
        builder.ConnectionStringBuilder.Timezone = "UTC";
        return builder.Build();
    }

    public override async Task<DbConnection> OpenAsync(CancellationToken ct) => await dataSource.OpenConnectionAsync(ct);

    /// <summary>REPEATABLE READ READ ONLY: one snapshot for every query of the comparison, writes refused.</summary>
    public override async Task<DbTransaction?> BeginSnapshotAsync(DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var nc = (NpgsqlConnection)connection;
        var tx = await nc.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var cmd = new NpgsqlCommand("SET TRANSACTION READ ONLY", nc, tx);
        await cmd.ExecuteNonQueryAsync(ct);
        return tx;
    }

    public override void AddParameter(DbCommand command, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ((NpgsqlCommand)command).Parameters.Add(new NpgsqlParameter(name, value));
    }

    public override object? ReadRaw(DbDataReader reader, int ordinal, ColumnPair column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return column.Kind switch
        {
            CanonicalKind.Number => reader.GetValue(ordinal),
            // timestamptz comes back as DateTime with Kind=Utc; timestamp and date as Kind=Unspecified.
            CanonicalKind.Date or CanonicalKind.Timestamp or CanonicalKind.TimestampTz => reader.GetDateTime(ordinal),
            _ => reader.GetString(ordinal),
        };
    }
}

/// <summary>Writes the accounts table on the PostgreSQL side of the simulated dual write.</summary>
public sealed class PostgresAccountWriter(NpgsqlConnection connection) : Audit.IAccountWriter
{
    public static async Task<PostgresAccountWriter> OpenAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new PostgresAccountWriter(await dataSource.OpenConnectionAsync(ct));
    }

    public async Task UpdateAccountAsync(long accountId, decimal balance, DateTime updatedAt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"UPDATE {Audit.AccountsSchema.PostgresTable} SET balance = @b, updated_at = @u WHERE account_id = @id", connection);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Numeric) { Value = balance });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(updatedAt, DateTimeKind.Unspecified) });
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = accountId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
