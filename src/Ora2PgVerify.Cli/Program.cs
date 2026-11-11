using System.CommandLine;
using System.Globalization;
using Ora2PgVerify.Audit;
using Ora2PgVerify.Cli;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Report;
using Ora2PgVerify.Scan;
using Ora2PgVerify.Scoring;
using Ora2PgVerify.Synthetic;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var log = Console.Error;
var flows = new Workflows(log);

// ---- shared options ------------------------------------------------------------------------------------
var oracleOpt = new Option<string?>("--oracle") { Description = $"Oracle read-only connection string (default: ${Connections.OracleEnv})" };
var postgresOpt = new Option<string?>("--postgres") { Description = $"PostgreSQL read-only connection string (default: ${Connections.PostgresEnv})" };
var oracleOwnerOpt = new Option<string?>("--oracle-owner") { Description = $"Oracle schema-owner connection string, seeding and the dual-write demo only (default: ${Connections.OracleOwnerEnv})" };
var postgresOwnerOpt = new Option<string?>("--postgres-owner") { Description = $"PostgreSQL owner connection string, seeding and the dual-write demo only (default: ${Connections.PostgresOwnerEnv})" };
var oracleSchemaOpt = new Option<string>("--oracle-schema") { Description = "Oracle schema (owner)", DefaultValueFactory = _ => SyntheticSchema.OracleSchema };
var postgresSchemaOpt = new Option<string>("--postgres-schema") { Description = "PostgreSQL schema", DefaultValueFactory = _ => SyntheticSchema.PostgresSchema };
var tablesOpt = new Option<string[]>("--tables") { Description = "Tables to compare (default: every table present on both sides)", AllowMultipleArgumentsPerToken = true };
var strategyOpt = new Option<string>("--strategy") { Description = "db (pushdown, default) or client (stream every row and hash in .NET)", DefaultValueFactory = _ => "db" };
var chunkOpt = new Option<long>("--chunk-rows") { Description = "Target rows per chunk (1,000 measured best on 1 M rows)", DefaultValueFactory = _ => 1_000 };
var hashOpt = new Option<string>("--hash") { Description = "md5 (default) or sha256", DefaultValueFactory = _ => "md5" };
var emptyAsNullOpt = new Option<bool>("--empty-as-null") { Description = "Treat '' in PostgreSQL as NULL instead of reporting it" };
var valuesOpt = new Option<bool>("--include-values") { Description = "Put the values of differing columns into the report (off: keys and column names only)" };
var expectOpt = new Option<string?>("--expect") { Description = "Defect manifest from 'seed': score recall and precision" };
var withScanOpt = new Option<bool>("--scan") { Description = "Also run the PL/SQL incompatibility scan" };
var showLiteralsOpt = new Option<bool>("--show-literals") { Description = "Show string literals in code snippets (off: replaced with '…')" };
var htmlOpt = new Option<string?>("--html") { Description = "Write the HTML report here" };
var jsonOpt = new Option<string?>("--json") { Description = "Write the JSON report here" };
var rowsOpt = new Option<int>("--rows") { Description = "Orders to generate (customers = rows/20, documents = rows/50)", DefaultValueFactory = _ => 100_000 };
var seedOpt = new Option<int>("--seed") { Description = "Random seed", DefaultValueFactory = _ => 42 };
var perKindOpt = new Option<int>("--per-kind") { Description = "Injected defects per kind", DefaultValueFactory = _ => 10 };
var manifestOpt = new Option<string>("--manifest") { Description = "Where to write the defect manifest", DefaultValueFactory = _ => "out/manifest.json" };
var oracleGranteeOpt = new Option<string?>("--oracle-grantee") { Description = "Role or user that gets SELECT on the seeded Oracle tables", DefaultValueFactory = _ => "VERIFY_READ" };
var postgresGranteeOpt = new Option<string?>("--postgres-grantee") { Description = "Role that gets SELECT on the seeded PostgreSQL tables", DefaultValueFactory = _ => "verify_reader" };
var secondsOpt = new Option<int>("--seconds") { Description = "Dual-write simulation length", DefaultValueFactory = _ => 20 };
var rateOpt = new Option<int>("--writes-per-second") { Description = "Dual-write simulation rate", DefaultValueFactory = _ => 100 };

CompareOptions CompareOptionsFrom(ParseResult p) => new()
{
    Strategy = (p.GetValue(strategyOpt) ?? "db").Equals("client", StringComparison.OrdinalIgnoreCase) ? CompareStrategy.ClientSide : CompareStrategy.DatabaseSide,
    ChunkRows = p.GetValue(chunkOpt),
    IncludeValues = p.GetValue(valuesOpt),
    Canonical = Workflows.Canonical(p.GetValue(emptyAsNullOpt), p.GetValue(hashOpt) ?? "md5"),
};

RunInfo RunInfoFrom(ParseResult p, string oracle, string postgres, CompareOptions o) => new(
    Workflows.Version,
    DateTimeOffset.UtcNow,
    Connections.OracleEndpoint(oracle),
    Connections.PostgresEndpoint(postgres),
    p.GetValue(oracleSchemaOpt) ?? SyntheticSchema.OracleSchema,
    p.GetValue(postgresSchemaOpt) ?? SyntheticSchema.PostgresSchema,
    o.Strategy,
    o.ChunkRows,
    o.Canonical,
    o.IncludeValues);

// ---- scan ----------------------------------------------------------------------------------------------
var scan = new Command("scan", "Report PL/SQL and schema constructs that need rework in PostgreSQL") { oracleOpt, oracleSchemaOpt, showLiteralsOpt, htmlOpt, jsonOpt };
scan.SetAction(async (p, ct) =>
{
    var oracle = Connections.Resolve(p.GetValue(oracleOpt), Connections.OracleEnv, "Oracle");
    var (result, _, _) = await flows.ScanAsync(oracle, p.GetValue(oracleSchemaOpt)!, new ScanOptions { ShowLiterals = p.GetValue(showLiteralsOpt) }, ct);
    var run = new RunInfo(Workflows.Version, DateTimeOffset.UtcNow, Connections.OracleEndpoint(oracle), "-", p.GetValue(oracleSchemaOpt)!, "-", CompareStrategy.DatabaseSide, 0, new(), false);
    await Workflows.WriteReportAsync(new VerificationReport(run, result, [], [], null, null), p.GetValue(htmlOpt), p.GetValue(jsonOpt), ct);
    foreach (var o in result.Objects.Take(15))
    {
        Console.WriteLine($"{o.Points,4} pts  {o.Findings,3} findings  {o.Type,-14} {o.Name}");
    }

    return 0;
});

// ---- compare -------------------------------------------------------------------------------------------
var compare = new Command("compare", "Compare table contents with chunked checksums and report the exact differing rows")
{
    oracleOpt, postgresOpt, oracleSchemaOpt, postgresSchemaOpt, tablesOpt, strategyOpt, chunkOpt, hashOpt, emptyAsNullOpt, valuesOpt, expectOpt, withScanOpt, showLiteralsOpt, htmlOpt, jsonOpt,
};
compare.SetAction(async (p, ct) =>
{
    var oracle = Connections.Resolve(p.GetValue(oracleOpt), Connections.OracleEnv, "Oracle");
    var postgres = Connections.Resolve(p.GetValue(postgresOpt), Connections.PostgresEnv, "PostgreSQL");
    var options = CompareOptionsFrom(p);
    var scanResult = p.GetValue(withScanOpt)
        ? (await flows.ScanAsync(oracle, p.GetValue(oracleSchemaOpt)!, new ScanOptions { ShowLiterals = p.GetValue(showLiteralsOpt) }, ct)).Result
        : null;
    var (tables, mapping) = await flows.CompareAsync(oracle, postgres, p.GetValue(oracleSchemaOpt)!, p.GetValue(postgresSchemaOpt)!, p.GetValue(tablesOpt), options, ct);
    DetectionScore? detection = null;
    if (p.GetValue(expectOpt) is { Length: > 0 } manifestPath)
    {
        detection = DetectionScorer.Score(await Workflows.ReadManifestAsync(manifestPath, ct), tables);
        Console.WriteLine($"detection: recall {detection.Recall:P1}, precision {detection.Precision:P1}, {detection.TruePositives}/{detection.Injected} found, {detection.FalsePositives} false positives");
    }

    var report = new VerificationReport(RunInfoFrom(p, oracle, postgres, options), scanResult, mapping, tables, detection, null);
    await Workflows.WriteReportAsync(report, p.GetValue(htmlOpt), p.GetValue(jsonOpt), ct);
    Console.WriteLine(report.RowsMatch ? "RESULT: rows match" : $"RESULT: {tables.Sum(t => t.Diffs.Count)} differing rows");
    return report.RowsMatch ? 0 : 1;
});

// ---- seed ----------------------------------------------------------------------------------------------
var seedCmd = new Command("seed", "Create the synthetic schema on both engines, load it and inject recorded defects")
{
    oracleOwnerOpt, postgresOwnerOpt, rowsOpt, seedOpt, perKindOpt, manifestOpt, oracleGranteeOpt, postgresGranteeOpt,
};
seedCmd.SetAction(async (p, ct) =>
{
    var manifest = await flows.SeedAsync(
        Connections.Resolve(p.GetValue(oracleOwnerOpt), Connections.OracleOwnerEnv, "Oracle owner"),
        Connections.Resolve(p.GetValue(postgresOwnerOpt), Connections.PostgresOwnerEnv, "PostgreSQL owner"),
        p.GetValue(rowsOpt), p.GetValue(seedOpt), p.GetValue(perKindOpt), p.GetValue(oracleGranteeOpt), p.GetValue(postgresGranteeOpt), ct);
    await Workflows.WriteManifestAsync(manifest, p.GetValue(manifestOpt)!, ct);
    return 0;
});

// ---- audit ---------------------------------------------------------------------------------------------
var audit = new Command("audit-demo", "Simulate a dual-write cut-over on the accounts table and audit it for divergence")
{
    oracleOpt, postgresOpt, oracleOwnerOpt, postgresOwnerOpt, seedOpt, secondsOpt, rateOpt, htmlOpt, jsonOpt,
};
audit.SetAction(async (p, ct) =>
{
    var oracle = Connections.Resolve(p.GetValue(oracleOpt), Connections.OracleEnv, "Oracle");
    var postgres = Connections.Resolve(p.GetValue(postgresOpt), Connections.PostgresEnv, "PostgreSQL");
    var section = await flows.AuditDemoAsync(oracle, postgres,
        Connections.Resolve(p.GetValue(oracleOwnerOpt), Connections.OracleOwnerEnv, "Oracle owner"),
        Connections.Resolve(p.GetValue(postgresOwnerOpt), Connections.PostgresOwnerEnv, "PostgreSQL owner"),
        p.GetValue(seedOpt),
        new SimulatorOptions { Duration = TimeSpan.FromSeconds(p.GetValue(secondsOpt)), WritesPerSecond = p.GetValue(rateOpt) },
        new AuditOptions(), ct);
    var options = new CompareOptions();
    var report = new VerificationReport(RunInfoFrom(p, oracle, postgres, options), null, [], [], null, section);
    await Workflows.WriteReportAsync(report, p.GetValue(htmlOpt), p.GetValue(jsonOpt), ct);
    return section.Score is { FalseNegatives: 0, FalsePositives: 0 } ? 0 : 1;
});

// ---- demo ----------------------------------------------------------------------------------------------
var demo = new Command("demo", "seed + scan + compare + score + dual-write audit, one HTML report (what docker compose runs)")
{
    oracleOpt, postgresOpt, oracleOwnerOpt, postgresOwnerOpt, rowsOpt, seedOpt, perKindOpt, secondsOpt, rateOpt, chunkOpt, valuesOpt, htmlOpt, jsonOpt,
};
demo.SetAction(async (p, ct) =>
{
    var oracle = Connections.Resolve(p.GetValue(oracleOpt), Connections.OracleEnv, "Oracle");
    var postgres = Connections.Resolve(p.GetValue(postgresOpt), Connections.PostgresEnv, "PostgreSQL");
    var oracleOwner = Connections.Resolve(p.GetValue(oracleOwnerOpt), Connections.OracleOwnerEnv, "Oracle owner");
    var postgresOwner = Connections.Resolve(p.GetValue(postgresOwnerOpt), Connections.PostgresOwnerEnv, "PostgreSQL owner");
    var options = CompareOptionsFrom(p);

    var manifest = await flows.SeedAsync(oracleOwner, postgresOwner, p.GetValue(rowsOpt), p.GetValue(seedOpt), p.GetValue(perKindOpt), "VERIFY_READ", "verify_reader", ct);
    var (scanResult, _, _) = await flows.ScanAsync(oracle, SyntheticSchema.OracleSchema, new ScanOptions(), ct);
    var (tables, mapping) = await flows.CompareAsync(oracle, postgres, SyntheticSchema.OracleSchema, SyntheticSchema.PostgresSchema, SyntheticSchema.ComparedTables, options, ct);
    var detection = DetectionScorer.Score(manifest, tables);
    Console.WriteLine($"detection: recall {detection.Recall:P1}, precision {detection.Precision:P1}, {detection.TruePositives}/{detection.Injected} injected defects found at the exact row, {detection.FalsePositives} false positives");
    var auditSection = await flows.AuditDemoAsync(oracle, postgres, oracleOwner, postgresOwner, p.GetValue(seedOpt),
        new SimulatorOptions { Duration = TimeSpan.FromSeconds(p.GetValue(secondsOpt)), WritesPerSecond = p.GetValue(rateOpt) }, new AuditOptions(), ct);

    var report = new VerificationReport(RunInfoFrom(p, oracle, postgres, options) with { OracleSchema = SyntheticSchema.OracleSchema, PostgresSchema = SyntheticSchema.PostgresSchema },
        scanResult, mapping, tables, detection, auditSection);
    var htmlPath = p.GetValue(htmlOpt) ?? "out/report.html";
    await Workflows.WriteReportAsync(report, htmlPath, p.GetValue(jsonOpt) ?? "out/report.json", ct);
    await Workflows.WriteManifestAsync(manifest, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(htmlPath))!, "manifest.json"), ct);

    var ok = detection is { FalseNegatives: 0, FalsePositives: 0 } && auditSection.Score is { FalseNegatives: 0, FalsePositives: 0 };
    Console.WriteLine($"dual-write: {auditSection.Score?.TruePositives}/{auditSection.Score?.Injected} faults confirmed, {auditSection.Score?.FalsePositives} false positives, {auditSection.Score?.Transients} transient differences ignored");
    Console.WriteLine(ok ? "DEMO_OK report written" : "DEMO_FAILED");
    return ok ? 0 : 1;
});

// ---- bench ---------------------------------------------------------------------------------------------
var runsOpt = new Option<int>("--runs") { Description = "Measured runs per configuration (after one warm-up)", DefaultValueFactory = _ => 5 };
var outOpt = new Option<string>("--out") { Description = "Directory for benchmark results", DefaultValueFactory = _ => "benchmarks/results" };
var noteOpt = new Option<string?>("--host-note") { Description = "Free text about host load, stored with the results" };
var groupsOpt = new Option<string[]>("--groups") { Description = "Only these groups: strategy, hash, chunk (default: all)", AllowMultipleArgumentsPerToken = true };
var bench = new Command("bench", "Measure strategies, chunk sizes and hash algorithms on an already seeded database")
{
    oracleOpt, postgresOpt, expectOpt, runsOpt, outOpt, noteOpt, groupsOpt,
};
bench.SetAction(async (p, ct) =>
{
    var oracle = Connections.Resolve(p.GetValue(oracleOpt), Connections.OracleEnv, "Oracle");
    var postgres = Connections.Resolve(p.GetValue(postgresOpt), Connections.PostgresEnv, "PostgreSQL");
    await new Bench(flows, log).RunAsync(oracle, postgres, p.GetValue(expectOpt), p.GetValue(runsOpt), p.GetValue(outOpt)!, p.GetValue(noteOpt), p.GetValue(groupsOpt) ?? [], ct);
    return 0;
});

var root = new RootCommand("ora2pg-verify: verify an Oracle to PostgreSQL migration (PL/SQL scan, chunked row comparison, dual-write audit)")
{
    scan, compare, seedCmd, audit, demo, bench,
};

try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception ex) when (ex is UsageException or IOException or System.Text.Json.JsonException)
{
    await log.WriteLineAsync("error: " + ex.Message);
    return 2;
}
