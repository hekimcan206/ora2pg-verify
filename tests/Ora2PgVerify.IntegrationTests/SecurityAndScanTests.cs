using Npgsql;
using Ora2PgVerify.Audit;
using Ora2PgVerify.Model;
using Ora2PgVerify.Oracle;
using Ora2PgVerify.Scan;
using Ora2PgVerify.Synthetic;
using Ora2PgVerify.UnitTests.Support;
using Oracle.ManagedDataAccess.Client;

namespace Ora2PgVerify.IntegrationTests;

public sealed class SecurityAndScanTests(Engines db)
{
    [Fact]
    public async Task The_oracle_reader_cannot_write()
    {
        await using var c = await OracleEngine.OpenOracleAsync(db.OracleReader, TestContext.Current.CancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE APP_OWNER.ORDERS SET AMOUNT = 0 WHERE ORDER_ID = 1";
        var act = () => cmd.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<OracleException>()).Which.Number.Should().BeOneOf([1031, 41900], "ORA-01031 insufficient privileges (23ai reports ORA-41900 missing UPDATE privilege)");
    }

    [Fact]
    public async Task The_postgres_reader_cannot_write()
    {
        await using var pg = NpgsqlDataSource.Create(db.PostgresReader);
        await using var cmd = pg.CreateCommand("UPDATE app.orders SET amount = 0 WHERE order_id = 1");
        var act = () => cmd.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().BeOneOf("25006", "42501"); // read-only transaction / insufficient privilege
    }

    [Fact]
    public async Task Each_side_reads_one_snapshot_for_the_whole_comparison()
    {
        var (specs, _) = await db.Flows.PlanAsync(db.OracleReader, db.PostgresSource(db.PostgresReader), SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, ["accounts"], TestContext.Current.CancellationToken);
        var spec = specs.Single();
        await using var ora = await db.OracleRows().OpenSessionAsync(TestContext.Current.CancellationToken);
        await using var pg = await db.PostgresRows().OpenSessionAsync(TestContext.Current.CancellationToken);
        var before = (await ora.GetKeyStatsAsync(spec, TestContext.Current.CancellationToken), await pg.GetKeyStatsAsync(spec, TestContext.Current.CancellationToken));

        await using (var c = await OracleEngine.OpenOracleAsync(db.OracleOwner, TestContext.Current.CancellationToken))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO ACCOUNTS VALUES (999999, 1, 'NEW', SYSTIMESTAMP)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var pgOwner = NpgsqlDataSource.Create(db.PostgresOwner))
        await using (var cmd = pgOwner.CreateCommand("INSERT INTO app.accounts VALUES (999999, 1, 'NEW', now())"))
        {
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var after = (await ora.GetKeyStatsAsync(spec, TestContext.Current.CancellationToken), await pg.GetKeyStatsAsync(spec, TestContext.Current.CancellationToken));
            after.Should().Be(before, "a comparison must not see rows committed after it started");
        }
        finally
        {
            await using var c = await OracleEngine.OpenOracleAsync(db.OracleOwner, TestContext.Current.CancellationToken);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM ACCOUNTS WHERE ACCOUNT_ID = 999999";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var pgOwner = NpgsqlDataSource.Create(db.PostgresOwner);
            await using var del = pgOwner.CreateCommand("DELETE FROM app.accounts WHERE account_id = 999999");
            await del.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Dictionary_scan_finds_what_the_offline_scan_of_the_script_finds()
    {
        var (result, view, _) = await db.Flows.ScanAsync(db.OracleReader, SyntheticSchema.OracleSchema, new ScanOptions(), TestContext.Current.CancellationToken);
        view.Should().Be("DBA_SOURCE", "the reader holds SELECT_CATALOG_ROLE");

        var offline = SchemaScanner.Scan(new SchemaFacts("APP_OWNER", SampleSchema.Objects(), [], [], [], [], [], []));
        static string Lines(IEnumerable<ScanFinding> f) => SampleSchema.Describe(f.Where(x => x.Line is not null).OrderBy(x => x.ObjectType, StringComparer.Ordinal)
            .ThenBy(x => x.ObjectName, StringComparer.Ordinal).ThenBy(x => x.Line).ThenBy(x => x.RuleId, StringComparer.Ordinal).ThenBy(x => x.Title, StringComparer.Ordinal));
        Lines(result.Findings).Should().Be(Lines(offline.Findings));

        result.Findings.Select(f => f.RuleId).Should().Contain(["SEQUENCE_MAXVALUE", "SYNONYM", "GLOBAL_TEMPORARY_TABLE", "TYPE_DATE", "INVALID_OBJECT"]);
        result.Objects.Single(o => o.Name == "PURGE_REMOTE_COPY").Status.Should().Be("INVALID");
        result.Objects.Single(o => o.Name == "BILLING_PKG" && o.Type == "PACKAGE BODY").Status.Should().Be("VALID");
    }

    [Fact]
    public async Task Dual_write_audit_confirms_every_fault_and_ignores_lag()
    {
        var section = await db.Flows.AuditDemoAsync(db.OracleReader, db.PostgresReader, db.OracleOwner, db.PostgresOwner, Engines.Seed,
            new SimulatorOptions { Accounts = 2_000, Duration = TimeSpan.FromSeconds(6), WritesPerSecond = 60, FaultsPerKind = 2 },
            new AuditOptions { RoundInterval = TimeSpan.FromMilliseconds(700) },
            TestContext.Current.CancellationToken);

        section.Score!.Injected.Should().Be(6);
        section.Score.TruePositives.Should().Be(6);
        section.Score.FalsePositives.Should().Be(0);
        section.Result.Transients.Should().NotBeEmpty("honest writes were lagging behind on PostgreSQL");
        section.Result.Confirmed.Should().OnlyContain(c => c.Kind == Compare.DiffKind.Changed && c.Columns.Count > 0);
        section.Result.Confirmed.SelectMany(c => c.Columns).Select(c => c.Column).Should().BeSubsetOf(["balance", "updated_at"]);
        section.Result.Rounds.Should().NotBeEmpty();
        (await db.Flows.PlanAsync(db.OracleReader, db.PostgresSource(db.PostgresReader), SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, ["accounts"], TestContext.Current.CancellationToken))
            .Specs.Single().Chunking.Should().Be(ChunkingMode.Range);
    }
}
