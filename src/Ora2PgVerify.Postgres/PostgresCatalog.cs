using System.Reflection;
using Npgsql;
using NpgsqlTypes;
using Ora2PgVerify.Model;
using Ora2PgVerify.Synthetic;

namespace Ora2PgVerify.Postgres;

/// <summary>Reads table definitions from information_schema.</summary>
public sealed class PostgresCatalog(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<string>> ListTablesAsync(string schema, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name");
        cmd.Parameters.AddWithValue("s", schema);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    public async Task<CatalogTable?> ReadTableAsync(string schema, string table, CancellationToken ct)
    {
        var columns = new List<CatalogColumn>();
        await using (var cmd = dataSource.CreateCommand(
            "SELECT column_name, data_type, character_maximum_length, numeric_precision, numeric_scale, datetime_precision, is_nullable " +
            "FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY ordinal_position"))
        {
            cmd.Parameters.AddWithValue("s", schema);
            cmd.Parameters.AddWithValue("t", table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var type = r.GetString(1);
                var isNumeric = type == "numeric";
                var isTime = type.StartsWith("timestamp", StringComparison.Ordinal);
                columns.Add(new CatalogColumn(
                    r.GetString(0),
                    type,
                    r.IsDBNull(2) ? null : r.GetInt32(2),
                    null,
                    isNumeric && !r.IsDBNull(3) ? r.GetInt32(3) : isTime && !r.IsDBNull(5) ? r.GetInt32(5) : null,
                    isNumeric && !r.IsDBNull(4) ? r.GetInt32(4) : null,
                    r.GetString(6) == "YES"));
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var pk = new List<string>();
        // pg_index, not information_schema: table_constraints hides constraints from roles that only hold SELECT.
        await using (var cmd = dataSource.CreateCommand(
            "SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey) " +
            "WHERE i.indrelid = to_regclass(format('%I.%I', @s::text, @t::text)) AND i.indisprimary ORDER BY array_position(i.indkey::int2[], a.attnum)"))
        {
            cmd.Parameters.AddWithValue("s", schema);
            cmd.Parameters.AddWithValue("t", table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                pk.Add(r.GetString(0));
            }
        }

        return new CatalogTable(schema, table, columns, pk);
    }
}

/// <summary>Creates and loads the synthetic target schema with the owner account (binary COPY).</summary>
public sealed class PostgresSeeder(NpgsqlDataSource ownerDataSource)
{
    public static string Script(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Ora2PgVerify.Postgres.Schema.{name}")
                           ?? throw new InvalidOperationException($"Missing embedded script {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task RecreateSchemaAsync(CancellationToken ct)
    {
        await using var cmd = ownerDataSource.CreateCommand(Script("tables.sql"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LoadAsync(SyntheticData data, DatasetSize size, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(size);
        await using var c = await ownerDataSource.OpenConnectionAsync(ct);

        await using (var w = await c.BeginBinaryImportAsync("COPY app.orders FROM STDIN (FORMAT BINARY)", ct))
        {
            var n = 0;
            foreach (var o in data.Orders(size.Orders, size.Customers))
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(o.OrderId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(o.CustomerCode, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(o.Status, NpgsqlDbType.Varchar, ct);
                await WriteNullableAsync(w, o.Note, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(o.Amount, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(o.DiscountRate, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(o.CreatedOn, NpgsqlDbType.Timestamp, ct);
                await WriteNullableAsync(w, o.ShippedAt, NpgsqlDbType.Timestamp, ct);
                await w.WriteAsync(o.UpdatedAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
                if (++n % 100_000 == 0)
                {
                    progress?.Report($"postgres orders {n:N0}");
                }
            }

            await w.CompleteAsync(ct);
        }

        await using (var w = await c.BeginBinaryImportAsync("COPY app.customers FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var x in data.Customers(size.Customers))
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(x.CustomerCode, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(x.FullName, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(x.City, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(x.CreditLimit, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(DateOnly.FromDateTime(x.BirthDate), NpgsqlDbType.Date, ct);
                await w.WriteAsync(x.Segment, NpgsqlDbType.Char, ct);
            }

            await w.CompleteAsync(ct);
        }

        await using (var w = await c.BeginBinaryImportAsync("COPY app.order_documents FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var x in data.Documents(size.Documents))
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(x.OrderId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)x.DocNo, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(x.Body, NpgsqlDbType.Text, ct);
                await w.WriteAsync(x.CreatedAt, NpgsqlDbType.Timestamp, ct);
            }

            await w.CompleteAsync(ct);
        }

        await LoadAccountsAsync(c, data, size, ct);
        await using var analyze = new NpgsqlCommand("ANALYZE app.orders, app.customers, app.order_documents, app.accounts", c);
        await analyze.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reloads only the accounts table (the dual-write demo starts from identical copies).</summary>
    public async Task ReloadAccountsAsync(SyntheticData data, DatasetSize size, CancellationToken ct)
    {
        await using var c = await ownerDataSource.OpenConnectionAsync(ct);
        await using (var truncate = new NpgsqlCommand("TRUNCATE app.accounts", c))
        {
            await truncate.ExecuteNonQueryAsync(ct);
        }

        await LoadAccountsAsync(c, data, size, ct);
    }

    private static async Task LoadAccountsAsync(NpgsqlConnection c, SyntheticData data, DatasetSize size, CancellationToken ct)
    {
        await using var w = await c.BeginBinaryImportAsync("COPY app.accounts FROM STDIN (FORMAT BINARY)", ct);
        foreach (var x in data.Accounts(size.Accounts))
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(x.AccountId, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(x.Balance, NpgsqlDbType.Numeric, ct);
            await w.WriteAsync(x.Status, NpgsqlDbType.Varchar, ct);
            await w.WriteAsync(x.UpdatedAt, NpgsqlDbType.Timestamp, ct);
        }

        await w.CompleteAsync(ct);
    }

    public async Task GrantReadAsync(string grantee, CancellationToken ct)
    {
        await using var cmd = ownerDataSource.CreateCommand($"GRANT SELECT ON ALL TABLES IN SCHEMA app TO {Sql.SqlDialect.Quote(grantee)}");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ExecuteAsync(SeedCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var cmd = ownerDataSource.CreateCommand(command.Sql);
        foreach (var p in command.Parameters)
        {
            cmd.Parameters.AddWithValue(p.Key, p.Value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter w, T? value, NpgsqlDbType type, CancellationToken ct) where T : class =>
        value is null ? w.WriteNullAsync(ct) : w.WriteAsync(value, type, ct);

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter w, T? value, NpgsqlDbType type, CancellationToken ct) where T : struct =>
        value is { } v ? w.WriteAsync(v, type, ct) : w.WriteNullAsync(ct);
}
