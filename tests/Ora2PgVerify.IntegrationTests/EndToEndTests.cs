using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Report;
using Ora2PgVerify.Scoring;
using Ora2PgVerify.Synthetic;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

namespace Ora2PgVerify.IntegrationTests;

/// <summary>The seeded schema on both engines, compared the way the CLI compares it, scored against the defect manifest.</summary>
public sealed class EndToEndTests(Engines db)
{
    private Task<(IReadOnlyList<TableComparison> Tables, IReadOnlyList<Model.MappingFinding> Mapping)> CompareAsync(CompareOptions o) =>
        db.Flows.CompareAsync(db.OracleReader, db.PostgresReader, SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, SyntheticSchema.ComparedTables, o, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Database_side_comparison_finds_every_injected_defect_at_the_exact_row_and_nothing_else()
    {
        var (tables, mapping) = await CompareAsync(new CompareOptions());
        var score = DetectionScorer.Score(db.Manifest, tables);

        score.MissedKeys.Should().BeEmpty();
        score.FalsePositiveKeys.Should().BeEmpty();
        score.Recall.Should().Be(1);
        score.Precision.Should().Be(1);
        score.ExactColumn.Should().Be(db.Manifest.Defects.Count);
        score.Kinds.Should().OnlyContain(k => k.ClassifiedAs == k.Injected);
        mapping.Should().Contain(m => m.Code == "DATE_TO_DATE" && m.Column == "birth_date");
        tables.Single(t => t.Table == "orders").Chunking.Should().Be(Model.ChunkingMode.Range);
    }

    [Fact]
    public async Task Client_side_and_sha256_find_the_same_rows()
    {
        var (db1, _) = await CompareAsync(new CompareOptions());
        var (client, _) = await CompareAsync(new CompareOptions { Strategy = CompareStrategy.ClientSide });
        var (sha, _) = await CompareAsync(new CompareOptions { Canonical = new CanonicalOptions(Hash: RowHashAlgorithm.Sha256), ChunkRows = 777 });

        static IEnumerable<string> Keys(IReadOnlyList<TableComparison> t) => t.SelectMany(x => x.Diffs.Select(d => $"{x.Table}:{d.Key}:{d.Kind}:{string.Join(",", d.Columns.Select(c => c.Column))}")).Order(StringComparer.Ordinal);
        Keys(client).Should().Equal(Keys(db1));
        Keys(sha).Should().Equal(Keys(db1));
        client.Sum(t => t.Source.RowsTransferred).Should().BeGreaterThanOrEqualTo(client.Sum(t => t.Source.Rows), "client-side streams every row");
        db1.Sum(t => t.Source.RowsTransferred).Should().BeLessThan(client.Sum(t => t.Source.RowsTransferred));
    }

    [Fact]
    public async Task Empty_as_null_policy_accepts_exactly_the_empty_string_defects()
    {
        var (tables, _) = await CompareAsync(new CompareOptions { Canonical = new CanonicalOptions(EmptyStringPolicy.AsNull) });
        var score = DetectionScorer.Score(db.Manifest, tables);

        score.MissedKeys.Should().HaveCount(Engines.PerKind).And.OnlyContain(k => k.Contains("ORD_EMPTY_NOTE", StringComparison.Ordinal));
        score.FalsePositives.Should().Be(0);
    }

    [Fact]
    public async Task Report_files_are_written_without_credentials()
    {
        var (tables, mapping) = await CompareAsync(new CompareOptions());
        var run = new RunInfo("test", DateTimeOffset.UtcNow, Cli.Connections.OracleEndpoint(db.OracleReader), Cli.Connections.PostgresEndpoint(db.PostgresReader),
            "APP_OWNER", "app", CompareStrategy.DatabaseSide, 10_000, CanonicalOptions.Default, false);
        var html = Path.Combine(Path.GetTempPath(), $"o2pv-{Guid.NewGuid():N}.html");
        var json = Path.ChangeExtension(html, ".json");
        await Cli.Workflows.WriteReportAsync(new VerificationReport(run, null, mapping, tables, DetectionScorer.Score(db.Manifest, tables), null), html, json, TestContext.Current.CancellationToken);

        foreach (var file in new[] { html, json })
        {
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            text.Should().NotContain(Engines.ReaderPassword).And.NotContain(Engines.OwnerPassword).And.NotContain("Password");
            File.Delete(file);
        }
    }
}
