using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Oracle;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

namespace Ora2PgVerify.IntegrationTests;

/// <summary>
/// The correctness argument of the database-side strategy: Oracle SQL, PostgreSQL SQL and the C# reference produce
/// the same canonical field for every value, including the awkward ones (negative fractions, 27-digit numbers,
/// midnight DATEs, offsets, Turkish and 4-byte UTF-8 text, trailing and leading blanks, all-blank CHAR, large CLOBs).
/// </summary>
public sealed class CanonicalParityTests(Engines db) : IAsyncLifetime
{
    private const int RowCount = 300;

    private static readonly string[] Texts =
    [
        "plain", "İzmir Işık ğüşöç", "trailing  ", "  leading", "~", ":", "0:", "1:a", "📦 kargo", "a", "Ağrı", "ŞEMSİ", "tab\tinside", "line\nbreak", "' quote",
    ];

    private static readonly string[] Chars = ["A", "AB      ", "        ", " X", "ç", "12345678"];

    public async ValueTask InitializeAsync()
    {
        await using (var c = await OracleEngine.OpenOracleAsync(db.OracleOwner, CancellationToken.None))
        {
            await Exec(c, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE PARITY PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;");
            await Exec(c, "CREATE TABLE PARITY (ID NUMBER(10) PRIMARY KEY, N NUMBER(38,10), I NUMBER(18,0), D DATE, TS TIMESTAMP(6), TZ TIMESTAMP(6) WITH TIME ZONE, " +
                          "V VARCHAR2(200 CHAR), C CHAR(8 CHAR), L CLOB)");
            await Exec(c, "GRANT SELECT ON PARITY TO VERIFY_READ");
        }

        await using var pg = NpgsqlDataSource.Create(db.PostgresOwner);
        await using (var cmd = pg.CreateCommand("DROP TABLE IF EXISTS app.parity; CREATE TABLE app.parity (id bigint PRIMARY KEY, n numeric(38,10), i bigint, d timestamp(0), " +
                                                "ts timestamp(6), tz timestamptz(6), v varchar(200), c char(8), l text)"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var rows = Rows().ToList();
        await using (var c = await OracleEngine.OpenOracleAsync(db.OracleOwner, CancellationToken.None))
        {
            foreach (var r in rows)
            {
                await using var cmd = c.CreateCommand();
                cmd.BindByName = true;
                cmd.CommandText = "INSERT INTO PARITY VALUES (:id, :n, :i, :d, :ts, :tz, :v, :c, :l)";
                cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Int64) { Value = r.Id });
                cmd.Parameters.Add(new OracleParameter("n", OracleDbType.Decimal) { Value = (object?)r.N ?? DBNull.Value });
                cmd.Parameters.Add(new OracleParameter("i", OracleDbType.Int64) { Value = r.I });
                cmd.Parameters.Add(new OracleParameter("d", OracleDbType.Date) { Value = r.D });
                cmd.Parameters.Add(new OracleParameter("ts", OracleDbType.TimeStamp) { Value = new OracleTimeStamp(r.Ts) });
                cmd.Parameters.Add(new OracleParameter("tz", OracleDbType.TimeStampTZ) { Value = new OracleTimeStampTZ(r.Tz.DateTime, (r.Tz.Offset < TimeSpan.Zero ? "-" : "+") + r.Tz.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)) });
                cmd.Parameters.Add(new OracleParameter("v", OracleDbType.Varchar2) { Value = (object?)r.V ?? DBNull.Value });
                cmd.Parameters.Add(new OracleParameter("c", OracleDbType.Char) { Value = r.C });
                cmd.Parameters.Add(new OracleParameter("l", OracleDbType.Clob) { Value = (object?)r.L ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await using var conn = await pg.OpenConnectionAsync();
        foreach (var r in rows)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO app.parity VALUES (@id, @n, @i, @d, @ts, @tz, @v, @c, @l)", conn);
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = r.Id });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Numeric) { Value = (object?)r.N ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Bigint) { Value = r.I });
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Timestamp) { Value = r.D });
            cmd.Parameters.Add(new NpgsqlParameter("ts", NpgsqlDbType.Timestamp) { Value = r.Ts });
            cmd.Parameters.Add(new NpgsqlParameter("tz", NpgsqlDbType.TimestampTz) { Value = r.Tz.UtcDateTime });
            // What Oracle stored for '' is NULL; the faithful copy holds NULL too.
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Varchar) { Value = string.IsNullOrEmpty(r.V) ? DBNull.Value : r.V });
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Char) { Value = r.C });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Text) { Value = (object?)r.L ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync();
        }

        // The table was created seconds ago; Oracle refuses a read-only snapshot older than that DDL (ORA-01466).
        // TableComparer retries this itself; the field-level test opens raw sessions, so wait until it settles.
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                await using var c = await OracleEngine.OpenOracleAsync(db.OracleReader, CancellationToken.None);
                await using var tx = c.BeginTransaction();
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SET TRANSACTION READ ONLY";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "SELECT COUNT(*) FROM APP_OWNER.PARITY";
                await cmd.ExecuteScalarAsync();
                break;
            }
            catch (OracleException ex) when (ex.Number == 1466)
            {
                await Task.Delay(1_000);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record ParityRow(long Id, decimal? N, long I, DateTime D, DateTime Ts, DateTimeOffset Tz, string? V, string C, string? L);

    private static IEnumerable<ParityRow> Rows()
    {
        var r = new Random(20261111);
        decimal[] special = [0m, 0.5m, -0.5m, 0.0000000001m, -0.0000000001m, 1m, -1m, 10m, 100.25m, 12345678901234567.1234567891m, -99999999999999999.9999999999m];
        for (var id = 1; id <= RowCount; id++)
        {
            decimal? n = id <= special.Length ? special[id - 1]
                : r.Next(10) == 0 ? null
                : decimal.Round((decimal)(r.NextDouble() - 0.5) * (decimal)Math.Pow(10, r.Next(0, 17)), r.Next(0, 11));
            var d = new DateTime(1900, 1, 1).AddDays(r.Next(0, 73_000));
            if (r.Next(3) != 0)
            {
                d = d.AddSeconds(r.Next(0, 86_400));
            }

            var ts = new DateTime(1970, 1, 1).AddTicks((long)(r.NextDouble() * 2_000_000_000) * TimeSpan.TicksPerSecond + (r.Next(0, 1_000_000) * 10L));
            var offset = TimeSpan.FromMinutes(r.Next(-12 * 4, 14 * 4) * 15);
            var tz = new DateTimeOffset(ts.AddDays(r.Next(-500, 500)), offset);
            var v = r.Next(8) == 0 ? null : r.Next(8) == 0 ? "" : Texts[r.Next(Texts.Length)] + (r.Next(2) == 0 ? "" : r.Next(1000).ToString(CultureInfo.InvariantCulture));
            var l = r.Next(6) == 0 ? null : string.Concat(Enumerable.Repeat(Texts[r.Next(Texts.Length)] + " ", r.Next(1, 2_500)));
            yield return new ParityRow(id, n, r.NextInt64(-999_999_999_999_999_999L, 999_999_999_999_999_999L), d, ts, tz, v, Chars[r.Next(Chars.Length)], l);
        }
    }

    private static async Task Exec(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<TableSpec> SpecAsync()
    {
        var (specs, findings) = await db.Flows.PlanAsync(db.OracleReader, db.PostgresSource(db.PostgresReader), "APP_OWNER", "app", ["parity"], TestContext.Current.CancellationToken);
        findings.Should().NotContain(f => f.Severity == Severity.High);
        return specs.Single();
    }

    public static TheoryData<EmptyStringPolicy, RowHashAlgorithm> Variants => new()
    {
        { EmptyStringPolicy.Distinct, RowHashAlgorithm.Md5 },
        { EmptyStringPolicy.AsNull, RowHashAlgorithm.Sha256 },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Oracle_sql_postgres_sql_and_csharp_agree_on_every_field(EmptyStringPolicy policy, RowHashAlgorithm hash)
    {
        var o = new CanonicalOptions(policy, hash);
        var spec = await SpecAsync();
        var keys = Enumerable.Range(1, RowCount).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        await using var ora = await db.OracleRows(o).OpenSessionAsync(TestContext.Current.CancellationToken);
        await using var pg = await db.PostgresRows(o).OpenSessionAsync(TestContext.Current.CancellationToken);

        var oraSql = await ora.GetRowFieldsAsync(spec, keys, clientSide: false, TestContext.Current.CancellationToken);
        var oraClient = await ora.GetRowFieldsAsync(spec, keys, clientSide: true, TestContext.Current.CancellationToken);
        var pgSql = await pg.GetRowFieldsAsync(spec, keys, clientSide: false, TestContext.Current.CancellationToken);
        var pgClient = await pg.GetRowFieldsAsync(spec, keys, clientSide: true, TestContext.Current.CancellationToken);

        var reference = oraClient.ToDictionary(r => r.Key);
        reference.Should().HaveCount(RowCount);
        foreach (var (name, rows) in new[] { ("Oracle SQL", oraSql), ("PostgreSQL SQL", pgSql), ("PostgreSQL C#", pgClient) })
        {
            rows.Should().HaveCount(RowCount, name);
            foreach (var row in rows)
            {
                var expected = reference[row.Key].Fields;
                for (var c = 0; c < spec.Columns.Count; c++)
                {
                    row.Fields[c].Should().Be(expected[c], $"{name}, row {row.Key}, column {spec.Columns[c].Name}");
                }
            }
        }
    }

    [Fact]
    public async Task Pushdown_row_digests_match_across_engines()
    {
        var spec = await SpecAsync();
        var result = await new TableComparer(new CompareOptions { ChunkRows = 37, LeafRows = 5 }).CompareAsync(spec, db.OracleRows(), db.PostgresRows(), TestContext.Current.CancellationToken);
        result.IsEqual.Should().BeTrue(string.Join(", ", result.Diffs.Take(5).Select(d => d.Key + ":" + string.Join("/", d.Columns.Select(c => c.Column)))));
        result.Source.Rows.Should().Be(RowCount);
    }
}
