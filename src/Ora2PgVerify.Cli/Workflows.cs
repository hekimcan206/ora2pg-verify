using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Ora2PgVerify.Audit;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Oracle;
using Ora2PgVerify.Postgres;
using Ora2PgVerify.Report;
using Ora2PgVerify.Scan;
using Ora2PgVerify.Synthetic;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

namespace Ora2PgVerify.Cli;

/// <summary>The end-to-end operations behind the CLI commands (also driven by the integration tests).</summary>
internal sealed class Workflows(TextWriter log)
{
    private void Log(string message) => log.WriteLine(message);

    public async Task<DefectManifest> SeedAsync(string oracleOwner, string postgresOwner, int rows, int seed, int perKind, string? oracleGrantee, string? postgresGrantee, CancellationToken ct)
    {
        var size = DatasetSize.ForOrders(rows);
        var data = new SyntheticData(seed);
        await using var pgOwner = PostgresEngine.CreateDataSource(postgresOwner);
        var ora = new OracleSeeder(oracleOwner);
        var pg = new PostgresSeeder(pgOwner);
        var progress = new Progress<string>(Log);

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(ora.RecreateSchemaAsync(ct), pg.RecreateSchemaAsync(ct));
        Log($"schema created ({sw.ElapsedMilliseconds} ms); loading {size.Orders:N0} orders, {size.Customers:N0} customers, {size.Documents:N0} documents, {size.Accounts:N0} accounts on both engines");
        sw.Restart();
        var oraLoad = Timed(() => ora.LoadAsync(data, size, progress, ct));
        var pgLoad = Timed(() => pg.LoadAsync(data, size, progress, ct));
        await Task.WhenAll(oraLoad, pgLoad);
        Log($"loaded: Oracle {(await oraLoad).TotalSeconds:0.0} s, PostgreSQL {(await pgLoad).TotalSeconds:0.0} s");

        await ora.CreatePlSqlObjectsAsync(ct);
        if (oracleGrantee is { Length: > 0 })
        {
            await ora.GrantReadAsync(oracleGrantee, ct);
        }

        if (postgresGrantee is { Length: > 0 })
        {
            await pg.GrantReadAsync(postgresGrantee, ct);
        }

        var (manifest, commands) = DefectPlanner.Plan(seed, size, perKind);
        foreach (var c in commands)
        {
            if (c.Side == Side.Oracle)
            {
                await ora.ExecuteAsync(c, ct);
            }
            else
            {
                await pg.ExecuteAsync(c, ct);
            }
        }

        Log($"injected {manifest.Defects.Count} defects ({manifest.Defects.Select(d => d.Code).Distinct().Count()} kinds)");
        return manifest;
    }

    public async Task<(ScanResult Result, string SourceView, TimeSpan Elapsed)> ScanAsync(string oracle, string schema, ScanOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (facts, view) = await new OracleCatalog(oracle).ReadSchemaFactsAsync(schema, ct);
        var result = SchemaScanner.Scan(facts, options);
        Log($"scan: {facts.Sources.Count} stored objects from {view}, {result.Findings.Count} findings in {sw.ElapsedMilliseconds} ms");
        return (result, view, sw.Elapsed);
    }

    /// <summary>Builds the table specs (and mapping findings) for the requested tables, or for every table present on both sides.</summary>
    public async Task<(IReadOnlyList<TableSpec> Specs, IReadOnlyList<MappingFinding> Findings)> PlanAsync(
        string oracle, NpgsqlDataSource pg, string oracleSchema, string postgresSchema, IReadOnlyList<string>? tables, CancellationToken ct)
    {
        var oraCatalog = new OracleCatalog(oracle);
        var pgCatalog = new PostgresCatalog(pg);
        var findings = new List<MappingFinding>();
        var oraTables = (await oraCatalog.ListTablesAsync(oracleSchema, ct)).Select(t => t.ToLowerInvariant()).ToList();
        var pgTables = (await pgCatalog.ListTablesAsync(postgresSchema, ct)).ToHashSet(StringComparer.Ordinal);
        var wanted = tables is { Count: > 0 } ? tables.Select(t => t.ToLowerInvariant()).ToList() : oraTables;

        var specs = new List<TableSpec>();
        foreach (var name in wanted)
        {
            if (!oraTables.Contains(name))
            {
                findings.Add(new MappingFinding(name, "", Severity.High, "TABLE_MISSING_IN_SOURCE", $"Table {name} not found in Oracle schema {oracleSchema}."));
                continue;
            }

            if (!pgTables.Contains(name))
            {
                findings.Add(new MappingFinding(name, "", Severity.High, "TABLE_MISSING_IN_TARGET", $"Table {name} has no PostgreSQL counterpart in schema {postgresSchema}."));
                continue;
            }

            var o = await oraCatalog.ReadTableAsync(oracleSchema, name.ToUpperInvariant(), ct);
            var p = await pgCatalog.ReadTableAsync(postgresSchema, name, ct);
            if (o is null || p is null)
            {
                continue;
            }

            var (spec, f) = TableSpecBuilder.Build(name, o, p);
            findings.AddRange(f);
            if (spec is not null)
            {
                specs.Add(spec);
            }
        }

        Log($"planned {specs.Count} table(s): {string.Join(", ", specs.Select(s => $"{s.Name} ({s.Chunking})"))}");
        return (specs, findings);
    }

    public async Task<(IReadOnlyList<TableComparison> Tables, IReadOnlyList<MappingFinding> Mapping)> CompareAsync(
        string oracle, string postgres, string oracleSchema, string postgresSchema, IReadOnlyList<string>? tables, CompareOptions options, CancellationToken ct)
    {
        await using var pg = PostgresEngine.CreateDataSource(postgres);
        var (specs, findings) = await PlanAsync(oracle, pg, oracleSchema, postgresSchema, tables, ct);
        var source = new DbRowSource(new OracleEngine(oracle), options.Canonical);
        var target = new DbRowSource(new PostgresEngine(pg), options.Canonical);
        var comparer = new TableComparer(options);
        var results = new List<TableComparison>();
        foreach (var spec in specs)
        {
            var r = await comparer.CompareAsync(spec, source, target, ct);
            Log(string.Create(CultureInfo.InvariantCulture,
                $"{spec.Name,-16} {r.Source.Rows,10:N0} / {r.Target.Rows,10:N0} rows  missing {r.Count(DiffKind.MissingInTarget),4}  extra {r.Count(DiffKind.ExtraInTarget),4}  changed {r.Count(DiffKind.Changed),4}  {r.Timings.Total.TotalMilliseconds,9:N0} ms ({r.Strategy}, {r.Chunking})"));
            results.Add(r);
        }

        return (results, findings);
    }

    public async Task<AuditSection> AuditDemoAsync(
        string oracleReader, string postgresReader, string oracleOwner, string postgresOwner, int seed, SimulatorOptions sim, AuditOptions audit, CancellationToken ct)
    {
        await using var pgReader = PostgresEngine.CreateDataSource(postgresReader);
        await using var pgOwner = PostgresEngine.CreateDataSource(postgresOwner);
        var size = DatasetSize.ForOrders(0, sim.Accounts);
        var data = new SyntheticData(seed);
        await Task.WhenAll(new OracleSeeder(oracleOwner).ReloadAccountsAsync(data, size, ct), new PostgresSeeder(pgOwner).ReloadAccountsAsync(data, size, ct));

        var (specs, _) = await PlanAsync(oracleReader, pgReader, SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, [SyntheticSchema.AccountsTable], ct);
        var spec = specs.Single();
        var auditor = new DualWriteAuditor(spec, new DbRowSource(new OracleEngine(oracleReader), audit.Compare.Canonical), new DbRowSource(new PostgresEngine(pgReader), audit.Compare.Canonical), audit);

        await using var oraWriter = await OracleAccountWriter.OpenAsync(oracleOwner, ct);
        await using var pgWriter = await PostgresAccountWriter.OpenAsync(pgOwner, ct);
        Log($"dual-write: {sim.WritesPerSecond} writes/s for {sim.Duration.TotalSeconds:0} s, PostgreSQL lag up to {sim.MaxLag.TotalMilliseconds:0} ms, {sim.FaultsPerKind} faults per kind");
        var simulation = Task.Run(() => new DualWriteSimulator(oraWriter, pgWriter, sim).RunAsync(ct), ct);

        // Rounds are never cancelled mid-query: the loop stops between rounds once the writes have settled.
        var round = 1;
        var stopAfter = int.MaxValue;
        while (round <= stopAfter && !ct.IsCancellationRequested)
        {
            var r = await auditor.RoundAsync(round, CancellationToken.None);
            if (r.NewlyConfirmed > 0)
            {
                Log($"round {r.Number}: {r.Differences} differing rows, {r.NewlyConfirmed} newly confirmed, {r.Converged} converged");
            }

            if (simulation.IsCompleted && stopAfter == int.MaxValue)
            {
                stopAfter = round + audit.ConfirmRounds + 1;
            }

            round++;
            await Task.Delay(audit.RoundInterval, ct);
        }

        var simResult = await simulation;
        var result = auditor.Result();
        var score = AuditScore.Of(simResult, result);
        Log($"dual-write audit: {simResult.Writes} writes, {simResult.Faults.Count} faults, {result.Confirmed.Count} confirmed (TP {score.TruePositives}, FP {score.FalsePositives}), {result.Transients.Count} transient differences");
        return new AuditSection(result, simResult, score);
    }

    public static async Task WriteReportAsync(VerificationReport report, string? htmlPath, string? jsonPath, CancellationToken ct)
    {
        if (htmlPath is { Length: > 0 })
        {
            EnsureDirectory(htmlPath);
            await File.WriteAllTextAsync(htmlPath, HtmlReportRenderer.Render(report), ct);
        }

        if (jsonPath is { Length: > 0 })
        {
            EnsureDirectory(jsonPath);
            await File.WriteAllTextAsync(jsonPath, report.ToJson(), ct);
        }
    }

    public static async Task WriteManifestAsync(DefectManifest manifest, string path, CancellationToken ct)
    {
        EnsureDirectory(path);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, VerificationReport.JsonOptions), ct);
    }

    public static async Task<DefectManifest> ReadManifestAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<DefectManifest>(await File.ReadAllTextAsync(path, ct), VerificationReport.JsonOptions)
        ?? throw new UsageException($"Cannot read manifest {path}.");

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static async Task<TimeSpan> Timed(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        return sw.Elapsed;
    }

    public static string Version => typeof(Workflows).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static CanonicalOptions Canonical(bool emptyAsNull, string hash) => new(
        emptyAsNull ? EmptyStringPolicy.AsNull : EmptyStringPolicy.Distinct,
        hash.Equals("sha256", StringComparison.OrdinalIgnoreCase) ? RowHashAlgorithm.Sha256 : RowHashAlgorithm.Md5);
}
