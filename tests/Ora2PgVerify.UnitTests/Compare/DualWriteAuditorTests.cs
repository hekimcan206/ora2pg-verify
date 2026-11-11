using Ora2PgVerify.Audit;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.UnitTests.Support;

namespace Ora2PgVerify.UnitTests.Compare;

public sealed class DualWriteAuditorTests
{
    private static readonly TableSpec Accounts = TableSpecBuilder.Build(
        "accounts",
        new CatalogTable("APP_OWNER", "ACCOUNTS", [new("ACCOUNT_ID", "NUMBER", null, 22, 10, 0, false), new("BALANCE", "NUMBER", null, 22, 14, 2, false)], ["ACCOUNT_ID"]),
        new CatalogTable("app", "accounts", [new("account_id", "bigint", null, null, null, null, false), new("balance", "numeric", null, null, 14, 2, false)], ["account_id"])).Spec!;

    private static List<object?[]> Rows(int n) => [.. Enumerable.Range(1, n).Select(i => new object?[] { (long)i, i * 1.5m })];

    private static void Set(List<object?[]> rows, long id, decimal balance) => rows[rows.FindIndex(r => (long)r[0]! == id)] = [id, balance];

    [Fact]
    public async Task Lag_converges_stable_divergence_is_confirmed_and_moving_rows_wait()
    {
        var oracle = new InMemoryRowSource("Oracle", Rows(500));
        var postgres = new InMemoryRowSource("PostgreSQL", Rows(500));
        var auditor = new DualWriteAuditor(Accounts, oracle, postgres, new AuditOptions { ConfirmRounds = 3, Compare = new CompareOptions { ChunkRows = 50, LeafRows = 10 } });
        var ct = TestContext.Current.CancellationToken;

        // Round 1: three writes reached Oracle only.
        Set(oracle.Rows, 10, 1m);
        Set(oracle.Rows, 20, 2m);
        Set(oracle.Rows, 30, 3m);
        (await auditor.RoundAsync(1, ct)).Differences.Should().Be(3);

        // Round 2: the lagging write for 10 arrives; 30 is updated again on both sides but PostgreSQL is behind again.
        Set(postgres.Rows, 10, 1m);
        Set(oracle.Rows, 30, 33m);
        Set(postgres.Rows, 30, 3m);
        var r2 = await auditor.RoundAsync(2, ct);
        r2.Converged.Should().Be(1);
        r2.Unstable.Should().Be(1, "row 30 changed its digests between rounds");

        // Rounds 3 and 4: nothing changes. Row 20 has now been stable and different for 3 rounds; 30 for 3 rounds by round 4.
        var r3 = await auditor.RoundAsync(3, ct);
        r3.NewlyConfirmed.Should().Be(1);
        var r4 = await auditor.RoundAsync(4, ct);
        r4.NewlyConfirmed.Should().Be(1);

        var result = auditor.Result();
        result.Confirmed.Select(c => (c.Key, c.Round)).Should().Equal(("20", 3), ("30", 4));
        result.Transients.Should().ContainSingle().Which.Key.Should().Be("10");
        result.StillUnstable.Should().Be(0);
    }

    [Fact]
    public async Task A_confirmed_row_that_gets_repaired_stays_reported()
    {
        var oracle = new InMemoryRowSource("Oracle", Rows(50));
        var postgres = new InMemoryRowSource("PostgreSQL", Rows(50));
        var auditor = new DualWriteAuditor(Accounts, oracle, postgres, new AuditOptions { ConfirmRounds = 2 });
        var ct = TestContext.Current.CancellationToken;
        postgres.Rows.RemoveAt(0);
        await auditor.RoundAsync(1, ct);
        await auditor.RoundAsync(2, ct);
        postgres.Rows.Insert(0, oracle.Rows[0]);
        await auditor.RoundAsync(3, ct);

        auditor.Result().Confirmed.Should().ContainSingle().Which.Kind.Should().Be(DiffKind.MissingInTarget);
        auditor.Result().Transients.Should().BeEmpty();
    }

    [Fact]
    public void Score_matches_confirmed_rows_to_injected_faults()
    {
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var sim = new SimulatorResult(100, [new InjectedFault(1, DualWriteFault.LostWrite, t0), new InjectedFault(2, DualWriteFault.WrongValue, t0)], TimeSpan.FromSeconds(10));
        var audit = new AuditResult("accounts", [], [
            new ConfirmedDivergence("1", "1", DiffKind.Changed, [], t0, t0.AddSeconds(3), 3),
            new ConfirmedDivergence("9", "9", DiffKind.Changed, [], t0, t0.AddSeconds(4), 4),
        ], [new TransientDifference("5", TimeSpan.FromMilliseconds(400), 1)], 0, new AuditOptions());

        var score = AuditScore.Of(sim, audit);
        score.TruePositives.Should().Be(1);
        score.FalsePositives.Should().Be(1);
        score.FalseNegatives.Should().Be(1);
        score.MedianTimeToConfirm.Should().Be(TimeSpan.FromSeconds(3));
        score.Precision.Should().Be(0.5);
    }
}
