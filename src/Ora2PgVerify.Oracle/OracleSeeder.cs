using System.Reflection;
using Ora2PgVerify.Synthetic;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Ora2PgVerify.Oracle;

/// <summary>Creates and loads the synthetic source schema with the owner account (array binding, 5,000 rows per round trip).</summary>
public sealed class OracleSeeder(string ownerConnectionString)
{
    private const int Batch = 5_000;

    public static string Script(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Ora2PgVerify.Oracle.Schema.{name}")
                           ?? throw new InvalidOperationException($"Missing embedded script {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Splits a script on lines that contain only "/" (SQL*Plus convention) and drops leading comment lines.</summary>
    public static IReadOnlyList<string> Statements(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var result = new List<string>();
        var current = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim() == "/")
            {
                var text = string.Join("\n", current.SkipWhile(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0)).Trim();
                if (text.Length > 0)
                {
                    result.Add(text);
                }

                current.Clear();
                continue;
            }

            current.Add(line);
        }

        return result;
    }

    public async Task RecreateSchemaAsync(CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(ownerConnectionString, ct);
        await ExecAsync(c, """
            BEGIN
              FOR o IN (SELECT object_name, object_type FROM user_objects
                         WHERE object_type IN ('TABLE','SEQUENCE','PACKAGE','PROCEDURE','FUNCTION','VIEW','TYPE','SYNONYM','MATERIALIZED VIEW')) LOOP
                BEGIN
                  EXECUTE IMMEDIATE 'DROP ' || o.object_type || ' "' || o.object_name || '"' ||
                    CASE o.object_type WHEN 'TABLE' THEN ' CASCADE CONSTRAINTS PURGE' WHEN 'TYPE' THEN ' FORCE' END;
                EXCEPTION WHEN OTHERS THEN NULL;
                END;
              END LOOP;
            END;
            """, ct);
        foreach (var statement in Statements(Script("tables.sql")))
        {
            await ExecAsync(c, statement, ct);
        }
    }

    /// <summary>PL/SQL objects are created after the load, so the sample trigger does not rewrite loaded rows.</summary>
    public async Task CreatePlSqlObjectsAsync(CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(ownerConnectionString, ct);
        foreach (var statement in Statements(Script("plsql.sql")))
        {
            // Some samples are invalid on purpose (a missing database link): Oracle stores them with errors, which is
            // what the scanner should see. ODP.NET reports "created with compilation errors" as ORA-24344.
            try
            {
                await ExecAsync(c, statement, ct);
            }
            catch (OracleException ex) when (ex.Number == 24344)
            {
            }
        }

        await ExecAsync(c, "ALTER TRIGGER ORDERS_BIU DISABLE", ct);
    }

    public async Task GrantReadAsync(string grantee, CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(ownerConnectionString, ct);
        foreach (var table in new[] { "ORDERS", "CUSTOMERS", "ORDER_DOCUMENTS", "ACCOUNTS" })
        {
            await ExecAsync(c, $"GRANT SELECT ON {table} TO {Sql.SqlDialect.Quote(grantee)}", ct);
        }
    }

    public async Task LoadAsync(SyntheticData data, DatasetSize size, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(size);
        await using var c = await OracleEngine.OpenOracleAsync(ownerConnectionString, ct);

        var loaded = 0;
        foreach (var batch in data.Orders(size.Orders, size.Customers).Chunk(Batch))
        {
            await using var cmd = ArrayCommand(c, "INSERT INTO ORDERS VALUES (:id, :cc, :st, :nt, :am, :dr, :co, :sa, :ua)", batch.Length);
            Add(cmd, "id", OracleDbType.Int64, batch.Select(o => (object)o.OrderId));
            Add(cmd, "cc", OracleDbType.Char, batch.Select(o => (object)o.CustomerCode));
            Add(cmd, "st", OracleDbType.Varchar2, batch.Select(o => (object)o.Status));
            Add(cmd, "nt", OracleDbType.Varchar2, batch.Select(o => (object?)o.Note));
            Add(cmd, "am", OracleDbType.Decimal, batch.Select(o => (object)o.Amount));
            Add(cmd, "dr", OracleDbType.Decimal, batch.Select(o => (object)o.DiscountRate));
            Add(cmd, "co", OracleDbType.Date, batch.Select(o => (object)o.CreatedOn));
            Add(cmd, "sa", OracleDbType.TimeStamp, batch.Select(o => o.ShippedAt is { } s ? (object)new OracleTimeStamp(s) : null));
            Add(cmd, "ua", OracleDbType.TimeStampTZ, batch.Select(o => (object)new OracleTimeStampTZ(o.UpdatedAt.DateTime, Offset(o.UpdatedAt.Offset))));
            await cmd.ExecuteNonQueryAsync(ct);
            loaded += batch.Length;
            if (loaded % 100_000 == 0)
            {
                progress?.Report($"oracle orders {loaded:N0}");
            }
        }

        foreach (var batch in data.Customers(size.Customers).Chunk(Batch))
        {
            await using var cmd = ArrayCommand(c, "INSERT INTO CUSTOMERS VALUES (:cc, :fn, :ci, :cl, :bd, :sg)", batch.Length);
            Add(cmd, "cc", OracleDbType.Varchar2, batch.Select(x => (object)x.CustomerCode));
            Add(cmd, "fn", OracleDbType.Varchar2, batch.Select(x => (object)x.FullName));
            Add(cmd, "ci", OracleDbType.Varchar2, batch.Select(x => (object)x.City));
            Add(cmd, "cl", OracleDbType.Decimal, batch.Select(x => (object)x.CreditLimit));
            Add(cmd, "bd", OracleDbType.Date, batch.Select(x => (object)x.BirthDate));
            Add(cmd, "sg", OracleDbType.Char, batch.Select(x => (object)x.Segment));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // CLOBs: smaller batches, each value a few kilobytes.
        foreach (var batch in data.Documents(size.Documents).Chunk(500))
        {
            await using var cmd = ArrayCommand(c, "INSERT INTO ORDER_DOCUMENTS VALUES (:oi, :dn, :bd, :ca)", batch.Length);
            Add(cmd, "oi", OracleDbType.Int64, batch.Select(x => (object)x.OrderId));
            Add(cmd, "dn", OracleDbType.Int32, batch.Select(x => (object)x.DocNo));
            Add(cmd, "bd", OracleDbType.Clob, batch.Select(x => (object)x.Body));
            Add(cmd, "ca", OracleDbType.TimeStamp, batch.Select(x => (object)new OracleTimeStamp(x.CreatedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await LoadAccountsAsync(c, data, size, ct);
        await ExecAsync(c, "BEGIN DBMS_STATS.GATHER_SCHEMA_STATS(USER); END;", ct);
    }

    /// <summary>Reloads only the accounts table (the dual-write demo starts from identical copies).</summary>
    public async Task ReloadAccountsAsync(SyntheticData data, DatasetSize size, CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(ownerConnectionString, ct);
        await ExecAsync(c, "TRUNCATE TABLE ACCOUNTS", ct);
        await LoadAccountsAsync(c, data, size, ct);
    }

    private static async Task LoadAccountsAsync(OracleConnection c, SyntheticData data, DatasetSize size, CancellationToken ct)
    {
        foreach (var batch in data.Accounts(size.Accounts).Chunk(Batch))
        {
            await using var cmd = ArrayCommand(c, "INSERT INTO ACCOUNTS VALUES (:id, :bl, :st, :ua)", batch.Length);
            Add(cmd, "id", OracleDbType.Int64, batch.Select(x => (object)x.AccountId));
            Add(cmd, "bl", OracleDbType.Decimal, batch.Select(x => (object)x.Balance));
            Add(cmd, "st", OracleDbType.Varchar2, batch.Select(x => (object)x.Status));
            Add(cmd, "ua", OracleDbType.TimeStamp, batch.Select(x => (object)new OracleTimeStamp(x.UpdatedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
        }

    }

    public async Task ExecuteAsync(SeedCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var c = await OracleEngine.OpenOracleAsync(ownerConnectionString, ct);
        await using var cmd = c.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = command.Sql;
        foreach (var p in command.Parameters)
        {
            cmd.Parameters.Add(new OracleParameter(p.Key, p.Value));
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static OracleCommand ArrayCommand(OracleConnection c, string sql, int rows)
    {
        var cmd = c.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = sql;
        cmd.ArrayBindCount = rows;
        return cmd;
    }

    private static void Add(OracleCommand cmd, string name, OracleDbType type, IEnumerable<object?> values)
    {
        var array = values.ToArray();
        var p = new OracleParameter(name, type) { Value = array.Select(v => v ?? DBNull.Value).ToArray() };
        p.ArrayBindStatus = array.Select(v => v is null ? OracleParameterStatus.NullInsert : OracleParameterStatus.Success).ToArray();
        cmd.Parameters.Add(p);
    }

    private static async Task ExecAsync(OracleConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
