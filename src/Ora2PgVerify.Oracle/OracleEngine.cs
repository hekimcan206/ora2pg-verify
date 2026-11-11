using System.Data;
using System.Data.Common;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Sql;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Ora2PgVerify.Oracle;

/// <summary>ODP.NET adapter for the generic row source.</summary>
public sealed class OracleEngine(string connectionString) : EngineAdapter
{
    public override SqlDialect Dialect => OracleDialect.Instance;

    public string ConnectionString { get; } = connectionString;

    public override async Task<DbConnection> OpenAsync(CancellationToken ct) => await OpenOracleAsync(ConnectionString, ct);

    /// <summary>Opens a connection with deterministic session settings: UTC session time zone and binary sort/compare.</summary>
    public static async Task<OracleConnection> OpenOracleAsync(string connectionString, CancellationToken ct)
    {
        var connection = new OracleConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER SESSION SET TIME_ZONE = 'UTC' NLS_SORT = BINARY NLS_COMP = BINARY NLS_NUMERIC_CHARACTERS = '.,'";
            await cmd.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// SET TRANSACTION READ ONLY gives transaction-level read consistency (one SCN for every query) and refuses DML.
    /// If the driver has already started the transaction (ORA-01453), SERIALIZABLE gives the same snapshot.
    /// Long comparisons on a busy source need enough undo retention, or they fail with ORA-01555.
    /// </summary>
    public override async Task<DbTransaction?> BeginSnapshotAsync(DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var oc = (OracleConnection)connection;
        var tx = oc.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            await using var cmd = oc.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SET TRANSACTION READ ONLY";
            await cmd.ExecuteNonQueryAsync(ct);
            return tx;
        }
        catch (OracleException ex) when (ex.Number == 1453)
        {
            await tx.RollbackAsync(ct);
            await tx.DisposeAsync();
            return oc.BeginTransaction(IsolationLevel.Serializable);
        }
    }

    public override void AddParameter(DbCommand command, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Parameters.Add(new OracleParameter(name, value));
    }

    public override void Configure(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var oc = (OracleCommand)command;
        oc.BindByName = true;
        oc.InitialLOBFetchSize = -1; // fetch CLOBs inline (client-side strategy reads them whole anyway)
    }

    public override void Configure(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var r = (OracleDataReader)reader;
        if (r.RowSize > 0)
        {
            // Default fetch is 128 KB per round trip; 4 MB keeps the client-side stream from being latency bound.
            r.FetchSize = Math.Max(r.RowSize * 2_000, 4L * 1024 * 1024);
        }
    }

    public override object? ReadRaw(DbDataReader reader, int ordinal, ColumnPair column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(column);
        var r = (OracleDataReader)reader;
        if (r.IsDBNull(ordinal))
        {
            return null;
        }

        return column.Kind switch
        {
            // OracleDecimal keeps all 38 digits; System.Decimal would overflow beyond 28.
            CanonicalKind.Number => r.GetOracleDecimal(ordinal).ToString(),
            CanonicalKind.Date => r.GetDateTime(ordinal),
            CanonicalKind.Timestamp => r.GetOracleTimeStamp(ordinal).Value,
            CanonicalKind.TimestampTz when column.OracleType.Contains("LOCAL", StringComparison.OrdinalIgnoreCase) =>
                DateTime.SpecifyKind(r.GetOracleTimeStampLTZ(ordinal).Value, DateTimeKind.Utc), // session time zone is UTC
            CanonicalKind.TimestampTz => DateTime.SpecifyKind(r.GetOracleTimeStampTZ(ordinal).ToUniversalTime().Value, DateTimeKind.Utc),
            _ => r.GetString(ordinal),
        };
    }

    public override long ReadInt64(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (long)((OracleDataReader)reader).GetOracleDecimal(ordinal).Value;
    }

    public override decimal ReadDecimal(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ((OracleDataReader)reader).GetOracleDecimal(ordinal).Value;
    }
}

/// <summary>Writes the accounts table on the Oracle side of the simulated dual write.</summary>
public sealed class OracleAccountWriter(OracleConnection connection) : Audit.IAccountWriter
{
    public static async Task<OracleAccountWriter> OpenAsync(string connectionString, CancellationToken ct) =>
        new(await OracleEngine.OpenOracleAsync(connectionString, ct));

    public async Task UpdateAccountAsync(long accountId, decimal balance, DateTime updatedAt, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = $"UPDATE {Audit.AccountsSchema.OracleTable} SET BALANCE = :b, UPDATED_AT = :u WHERE ACCOUNT_ID = :id";
        cmd.Parameters.Add(new OracleParameter("b", OracleDbType.Decimal) { Value = balance });
        cmd.Parameters.Add(new OracleParameter("u", OracleDbType.TimeStamp) { Value = new OracleTimeStamp(updatedAt) });
        cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Int64) { Value = accountId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
