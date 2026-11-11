using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Oracle;
using Ora2PgVerify.Postgres;
using Ora2PgVerify.Report;
using Ora2PgVerify.Scan;
using Ora2PgVerify.Scoring;
using Ora2PgVerify.Synthetic;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

namespace Ora2PgVerify.Cli;

internal sealed record BenchRow(
    string Group,
    string Configuration,
    string Table,
    long Rows,
    int Runs,
    double MedianMs,
    double MinMs,
    double MaxMs,
    double FingerprintMs,
    double DrillDownMs,
    long Queries,
    long RowsTransferred,
    int DifferingRows,
    double OracleBusyMs,
    double PostgresBusyMs);

internal sealed record BenchResult(string Environment, string? HostNote, DateTimeOffset At, IReadOnlyList<BenchRow> Rows, DetectionScore? Detection, double ScanMedianMs, int ScanFindings);

/// <summary>Repeatable measurements on a seeded database: one warm-up run, then the median of N runs per configuration.</summary>
internal sealed class Bench(Workflows flows, TextWriter log)
{
    public async Task RunAsync(string oracle, string postgres, string? manifestPath, int runs, string outDir, string? note, IReadOnlyCollection<string> groups, CancellationToken ct)
    {
        await using var pg = PostgresEngine.CreateDataSource(postgres);
        var (specs, _) = await flows.PlanAsync(oracle, pg, SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, SyntheticSchema.ComparedTables, ct);
        var orders = specs.Single(s => s.Name == "orders");
        var rows = new List<BenchRow>();

        async Task Measure(string group, string config, TableSpec spec, CompareOptions o, int n)
        {
            if (groups.Count > 0 && !groups.Contains(group))
            {
                return;
            }

            var source = new DbRowSource(new OracleEngine(oracle), o.Canonical);
            var target = new DbRowSource(new PostgresEngine(pg), o.Canonical);
            var comparer = new TableComparer(o);
            await comparer.CompareAsync(spec, source, target, ct); // warm-up: caches, connection pools, JIT
            var results = new List<TableComparison>();
            for (var i = 0; i < n; i++)
            {
                results.Add(await comparer.CompareAsync(spec, source, target, ct));
            }

            var totals = results.Select(r => r.Timings.Total.TotalMilliseconds).Order().ToList();
            var median = results.OrderBy(r => r.Timings.Total).ElementAt(results.Count / 2);
            var row = new BenchRow(group, config, spec.Name, Math.Max(median.Source.Rows, median.Target.Rows), n, totals[n / 2], totals[0], totals[^1],
                median.Timings.Fingerprint.TotalMilliseconds, median.Timings.DrillDown.TotalMilliseconds,
                median.Source.Queries + median.Target.Queries, median.Source.RowsTransferred + median.Target.RowsTransferred, median.Diffs.Count,
                median.Source.Busy.TotalMilliseconds, median.Target.Busy.TotalMilliseconds);
            rows.Add(row);
            log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{group,-9} {config,-34} {spec.Name,-16} median {row.MedianMs,9:N0} ms  (min {row.MinMs:N0}, max {row.MaxMs:N0})  transferred {row.RowsTransferred,9:N0}  diffs {row.DifferingRows}"));
        }

        var md5 = new CanonicalOptions();
        foreach (var spec in specs)
        {
            await Measure("strategy", "database-side, MD5, 10k-row chunks", spec, new CompareOptions { ChunkRows = 10_000, Canonical = md5 }, runs);
            await Measure("strategy", "client-side, MD5", spec, new CompareOptions { Strategy = CompareStrategy.ClientSide, Canonical = md5 }, runs);
        }

        await Measure("hash", "database-side, SHA-256, 10k-row chunks", orders, new CompareOptions { ChunkRows = 10_000, Canonical = md5 with { Hash = RowHashAlgorithm.Sha256 } }, runs);

        foreach (var chunk in new long[] { 500, 1_000, 2_000, 5_000, 10_000, 100_000, 1_000_000 })
        {
            await Measure("chunk", string.Create(CultureInfo.InvariantCulture, $"database-side, {chunk:N0}-row chunks"), orders, new CompareOptions { ChunkRows = chunk, Canonical = md5 }, Math.Min(runs, 3));
        }

        var customers = specs.Single(s => s.Name == "customers");
        foreach (var chunk in new long[] { 100, 1_000, 10_000 })
        {
            await Measure("chunk", string.Create(CultureInfo.InvariantCulture, $"database-side, {chunk:N0}-row buckets"), customers, new CompareOptions { ChunkRows = chunk, Canonical = md5 }, Math.Min(runs, 3));
        }

        var scans = new List<double>();
        var findings = 0;
        for (var i = 0; i <= Math.Min(runs, 3); i++)
        {
            var (result, _, elapsed) = await flows.ScanAsync(oracle, SyntheticSchema.OracleSchema, new ScanOptions(), ct);
            findings = result.Findings.Count;
            if (i > 0)
            {
                scans.Add(elapsed.TotalMilliseconds);
            }
        }

        DetectionScore? detection = null;
        if (manifestPath is { Length: > 0 })
        {
            var (tables, _) = await flows.CompareAsync(oracle, postgres, SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, SyntheticSchema.ComparedTables, new CompareOptions(), ct);
            detection = DetectionScorer.Score(await Workflows.ReadManifestAsync(manifestPath, ct), tables);
        }

        var env = $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} logical CPUs; .NET {Environment.Version}";
        var result2 = new BenchResult(env, note, DateTimeOffset.UtcNow, rows, detection, scans.Order().ElementAt(scans.Count / 2), findings);
        Directory.CreateDirectory(outDir);
        var name = groups.Count > 0 ? "bench-" + string.Join("-", groups) : "bench";
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".json"), JsonSerializer.Serialize(result2, VerificationReport.JsonOptions), ct);
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".md"), Markdown(result2), ct);
        log.WriteLine($"results written to {outDir}");
    }

    private static string Markdown(BenchResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Measured {r.At:yyyy-MM-dd HH:mm} UTC on {r.Environment}.");
        if (r.HostNote is { Length: > 0 })
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Host: {r.HostNote}");
        }

        sb.AppendLine().AppendLine("| group | configuration | table | rows | runs | median ms | min | max | fingerprint ms | drill-down ms | Oracle busy ms | PostgreSQL busy ms | queries | rows transferred | differing rows |")
          .AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var x in r.Rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {x.Group} | {x.Configuration} | {x.Table} | {x.Rows:N0} | {x.Runs} | {x.MedianMs:N0} | {x.MinMs:N0} | {x.MaxMs:N0} | {x.FingerprintMs:N0} | {x.DrillDownMs:N0} | {x.OracleBusyMs:N0} | {x.PostgresBusyMs:N0} | {x.Queries:N0} | {x.RowsTransferred:N0} | {x.DifferingRows} |");
        }

        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"PL/SQL scan: median {r.ScanMedianMs:N0} ms, {r.ScanFindings} findings.");
        if (r.Detection is { } d)
        {
            sb.AppendLine().AppendLine("| defect | table | injected | detected | exact column | classified |").AppendLine("|---|---|---:|---:|---:|---:|");
            foreach (var k in d.Kinds)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {k.Code} | {k.Table} | {k.Injected} | {k.Detected} | {k.ExactColumn} | {k.ClassifiedAs} |");
            }

            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Recall {d.Recall:P1}, precision {d.Precision:P1}, false positives {d.FalsePositives}, missed {d.FalseNegatives}.");
        }

        return sb.ToString();
    }
}
