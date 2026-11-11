using System.Globalization;
using Ora2PgVerify.Audit;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Report;
using Ora2PgVerify.Scan;
using Ora2PgVerify.Scoring;
using Ora2PgVerify.Synthetic;
using Ora2PgVerify.UnitTests.Scan;
using Ora2PgVerify.UnitTests.Support;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

namespace Ora2PgVerify.UnitTests.Report;

public sealed class ReportTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-11T09:30:00Z", CultureInfo.InvariantCulture);

    /// <summary>A fixed report covering every section; built from deterministic inputs so the HTML is byte-stable.</summary>
    internal static async Task<VerificationReport> SampleAsync(bool includeValues)
    {
        var src = TestRows.OracleOrders(400);
        var tgt = TestRows.PostgresOrders(400);
        tgt.RemoveAt(3);
        tgt[50] = TestRows.With(tgt[50], 4, (decimal)tgt[50][4]! + 0.01m);
        tgt[60] = TestRows.With(tgt[60], 3, tgt[60][3] is null ? "" : "<script>alert(1)</script>");
        var comparison = await new TableComparer(new CompareOptions { ChunkRows = 100, LeafRows = 10, IncludeValues = includeValues })
            .CompareAsync(TestSpecs.Orders, new InMemoryRowSource("Oracle", src), new InMemoryRowSource("PostgreSQL", tgt));
        comparison = comparison with { Timings = new PhaseTimings(TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(60)) };

        var scan = SchemaScanner.Scan(new SchemaFacts("APP_OWNER", ScannerTests.SampleObjects(), [], [], [], [], [], []));
        var mapping = TableSpecBuilder.Build("orders", TestSpecs.OracleOrders, TestSpecs.PostgresOrders).Findings;
        var manifest = new DefectManifest(1, 1, DatasetSize.ForOrders(400),
        [
            new InjectedDefect("ORD_MISSING", "orders", "4", "4", DiffKind.MissingInTarget, null, null),
            new InjectedDefect("ORD_AMOUNT_DRIFT", "orders", "51", "51", DiffKind.Changed, "amount", ValueDifference.NumericDrift),
        ]);
        var detection = DetectionScorer.Score(manifest, [comparison]);
        var audit = new AuditSection(
            new AuditResult("accounts",
                [new AuditRound(1, At, TimeSpan.FromMilliseconds(80), 5, 0, 0, 0), new AuditRound(2, At.AddSeconds(1), TimeSpan.FromMilliseconds(70), 1, 1, 4, 0)],
                [new ConfirmedDivergence("42", "42", DiffKind.Changed, [new ColumnDiff("balance", ValueDifference.NumericDrift, "+0.01", null, null)], At, At.AddSeconds(1), 2)],
                [new TransientDifference("7", TimeSpan.FromMilliseconds(350), 1)], 0, new AuditOptions()),
            new SimulatorResult(1000, [new InjectedFault(42, DualWriteFault.WrongValue, At)], TimeSpan.FromSeconds(10)),
            null);
        audit = audit with { Score = AuditScore.Of(audit.Simulation!, audit.Result) };

        var run = new RunInfo("1.0.0", At, "oracle:1521/FREEPDB1", "postgres:5432/appdb", "APP_OWNER", "app", CompareStrategy.DatabaseSide, 100, CanonicalOptions.Default, includeValues);
        return new VerificationReport(run, scan, mapping, [comparison], detection, audit);
    }

    [Fact]
    public async Task Html_report_matches_golden() =>
        Golden.Verify("report.html", HtmlReportRenderer.Render(await SampleAsync(includeValues: false)));

    [Fact]
    public async Task Report_is_deterministic_and_self_contained()
    {
        var a = HtmlReportRenderer.Render(await SampleAsync(false));
        var b = HtmlReportRenderer.Render(await SampleAsync(false));
        a.Should().Be(b);
        a.Should().NotContain("<script").And.NotContain("http://").And.NotContain("<link ").And.NotContain("src=");
    }

    [Fact]
    public async Task Values_are_html_encoded_when_included_and_absent_otherwise()
    {
        var with = HtmlReportRenderer.Render(await SampleAsync(includeValues: true));
        var without = HtmlReportRenderer.Render(await SampleAsync(includeValues: false));
        with.Should().Contain("&lt;script&gt;").And.NotContain("<script>alert");
        without.Should().NotContain("alert(1)");
    }

    [Fact]
    public async Task Json_report_round_trips_enums_as_text()
    {
        var json = (await SampleAsync(false)).ToJson();
        json.Should().Contain("\"kind\": \"MissingInTarget\"").And.Contain("\"strategy\": \"DatabaseSide\"");
    }
}
