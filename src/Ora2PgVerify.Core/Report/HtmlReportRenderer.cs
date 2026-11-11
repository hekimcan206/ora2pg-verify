using System.Globalization;
using System.Net;
using System.Text;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Scan;

namespace Ora2PgVerify.Report;

/// <summary>
/// Renders a <see cref="VerificationReport"/> as one self-contained HTML file (inline CSS, no scripts, no external
/// requests), so it can be attached to a change ticket or opened offline. Output is deterministic for a given
/// report, which is what the golden test relies on.
/// </summary>
public static class HtmlReportRenderer
{
    public const int MaxRowsPerTable = 200;

    public static string Render(VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var h = new StringBuilder(64 * 1024);
        var run = report.Run;
        var totalDiffs = report.Tables.Sum(t => t.Diffs.Count);
        var verdictOk = report.RowsMatch && (report.Audit?.Result.Confirmed.Count ?? 0) == 0;

        h.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
         .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
         .Append("<title>ora2pg-verify report</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        h.Append("<header>\n<p class=\"eyebrow\">ora2pg-verify ").Append(E(run.Version)).Append(" · Oracle → PostgreSQL migration verification</p>\n")
         .Append("<h1>").Append(verdictOk ? "Data matches" : "Differences found").Append("</h1>\n")
         .Append("<p class=\"meta\">Generated ").Append(E(run.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)))
         .Append(" · Oracle <code>").Append(E(run.OracleEndpoint)).Append("</code> schema <code>").Append(E(run.OracleSchema))
         .Append("</code> · PostgreSQL <code>").Append(E(run.PostgresEndpoint)).Append("</code> schema <code>").Append(E(run.PostgresSchema)).Append("</code></p>\n</header>\n");

        // ---- summary cards
        h.Append("<section class=\"cards\">\n");
        Card(h, verdictOk ? "ok" : "bad", verdictOk ? "PASS" : "FAIL", "verdict");
        Card(h, "", N(report.Tables.Sum(t => Math.Max(t.Source.Rows, t.Target.Rows))), "rows compared in " + N(report.Tables.Count) + " tables");
        Card(h, totalDiffs == 0 ? "ok" : "bad", N(totalDiffs), "differing rows");
        if (report.Scan is { } scan)
        {
            Card(h, scan.Count(Severity.High) == 0 ? "" : "warn", N(scan.Findings.Count), $"PL/SQL & schema findings ({N(scan.Count(Severity.High))} high)");
        }

        if (report.Detection is { } det)
        {
            Card(h, det.Recall >= 1 && det.Precision >= 1 ? "ok" : "bad", $"{P(det.Recall)} / {P(det.Precision)}", "recall / precision on injected defects");
        }

        if (report.Audit is { } audit)
        {
            Card(h, audit.Result.Confirmed.Count == 0 ? "ok" : "bad", N(audit.Result.Confirmed.Count), "confirmed dual-write divergences");
        }

        h.Append("</section>\n");

        h.Append("<nav>").Append(report.Scan is null ? "" : "<a href=\"#scan\">Incompatibilities</a>")
         .Append("<a href=\"#mapping\">Type mapping</a><a href=\"#rows\">Row comparison</a>")
         .Append(report.Detection is null ? "" : "<a href=\"#detection\">Detection</a>")
         .Append(report.Audit is null ? "" : "<a href=\"#audit\">Dual-write audit</a>")
         .Append("<a href=\"#method\">Method</a></nav>\n");

        if (report.Scan is { } s)
        {
            RenderScan(h, s);
        }

        RenderMapping(h, report.Mapping);
        RenderTables(h, report.Tables, run.ValuesIncluded);
        if (report.Detection is { } d)
        {
            RenderDetection(h, d);
        }

        if (report.Audit is { } a)
        {
            RenderAudit(h, a);
        }

        RenderMethod(h, run);
        h.Append("</main>\n</body>\n</html>\n");
        return h.ToString();
    }

    private static void RenderScan(StringBuilder h, ScanResult s)
    {
        h.Append("<section id=\"scan\">\n<h2>PL/SQL and schema incompatibilities</h2>\n")
         .Append("<p class=\"lede\">Constructs in schema <code>").Append(E(s.Schema)).Append("</code> that need rework before they run on PostgreSQL. ")
         .Append("Effort points are relative (Low 1, Medium 3, High 8 per occurrence), in the spirit of ora2pg's cost units, not person-days.</p>\n");
        h.Append("<div class=\"pills\">");
        foreach (var sev in new[] { Severity.High, Severity.Medium, Severity.Low, Severity.Info })
        {
            h.Append("<span class=\"pill ").Append(Sev(sev)).Append("\">").Append(sev).Append(' ').Append(N(s.Count(sev))).Append("</span>");
        }

        h.Append("<span class=\"pill\">").Append(N(s.TotalPoints)).Append(" effort points</span></div>\n");

        h.Append("<h3>Objects by effort</h3>\n<div class=\"scroll\"><table>\n<thead><tr><th>Object</th><th>Type</th><th>Status</th><th class=\"num\">Lines</th><th class=\"num\">Findings</th><th class=\"num\">Points</th><th>Worst</th></tr></thead>\n<tbody>\n");
        foreach (var o in s.Objects)
        {
            h.Append("<tr><td><code>").Append(E(o.Name)).Append("</code></td><td>").Append(E(o.Type)).Append("</td><td>").Append(E(o.Status))
             .Append("</td><td class=\"num\">").Append(o.Lines == 0 ? "" : N(o.Lines)).Append("</td><td class=\"num\">").Append(N(o.Findings))
             .Append("</td><td class=\"num\">").Append(N(o.Points)).Append("</td><td>").Append(o.Worst is { } w ? Badge(w) : "").Append("</td></tr>\n");
        }

        h.Append("</tbody></table></div>\n");

        h.Append("<h3>Findings</h3>\n<div class=\"scroll\"><table class=\"findings\">\n<thead><tr><th>Severity</th><th>Construct</th><th>Where</th><th>Code</th><th>Suggested rewrite</th></tr></thead>\n<tbody>\n");
        foreach (var f in s.Findings)
        {
            h.Append("<tr><td>").Append(Badge(f.Severity)).Append("</td><td><strong>").Append(E(f.Title)).Append("</strong><br><span class=\"muted\">").Append(E(f.RuleId))
             .Append("</span></td><td><code>").Append(E(f.ObjectName)).Append("</code><br><span class=\"muted\">").Append(E(f.ObjectType))
             .Append(f.Line is { } l ? " · line " + N(l) : "").Append("</span></td><td>")
             .Append(f.Snippet is { Length: > 0 } sn ? "<code class=\"snippet\">" + E(sn) + "</code>" : "")
             .Append("</td><td>").Append(E(f.Suggestion)).Append(" <a href=\"").Append(E(f.Reference)).Append("\">ref</a></td></tr>\n");
        }

        h.Append("</tbody></table></div>\n</section>\n");
    }

    private static void RenderMapping(StringBuilder h, IReadOnlyList<MappingFinding> mapping)
    {
        h.Append("<section id=\"mapping\">\n<h2>Column type mapping</h2>\n");
        if (mapping.Count == 0)
        {
            h.Append("<p class=\"lede\">No mapping observations.</p>\n</section>\n");
            return;
        }

        h.Append("<p class=\"lede\">How each compared column was mapped, and where the mapping changes meaning. Comparison semantics follow the Oracle type.</p>\n")
         .Append("<div class=\"scroll\"><table>\n<thead><tr><th>Severity</th><th>Column</th><th>Code</th><th>Observation</th></tr></thead>\n<tbody>\n");
        foreach (var m in mapping.OrderByDescending(m => m.Severity).ThenBy(m => m.Table, StringComparer.Ordinal).ThenBy(m => m.Column, StringComparer.Ordinal).ThenBy(m => m.Code, StringComparer.Ordinal))
        {
            h.Append("<tr><td>").Append(Badge(m.Severity)).Append("</td><td><code>").Append(E(m.Table)).Append('.').Append(E(m.Column)).Append("</code></td><td class=\"muted\">")
             .Append(E(m.Code)).Append("</td><td>").Append(E(m.Message)).Append("</td></tr>\n");
        }

        h.Append("</tbody></table></div>\n</section>\n");
    }

    private static void RenderTables(StringBuilder h, IReadOnlyList<TableComparison> tables, bool values)
    {
        h.Append("<section id=\"rows\">\n<h2>Row comparison</h2>\n")
         .Append("<p class=\"lede\">Chunk fingerprints (row count and sum of 60-bit row hashes) computed on each engine; mismatched chunks are split until the differing rows are isolated.")
         .Append(values ? " Values of differing columns are included (<code>--include-values</code>)." : " Values are not included: only keys, column names and the kind of difference (<code>--include-values</code> to opt in).")
         .Append("</p>\n");

        h.Append("<div class=\"scroll\"><table>\n<thead><tr><th>Table</th><th>Chunking</th><th class=\"num\">Oracle rows</th><th class=\"num\">PostgreSQL rows</th><th class=\"num\">Chunks</th><th class=\"num\">Bad chunks</th>")
         .Append("<th class=\"num\">Missing</th><th class=\"num\">Extra</th><th class=\"num\">Changed</th><th class=\"num\">Rows transferred</th><th class=\"num\">Time</th></tr></thead>\n<tbody>\n");
        foreach (var t in tables)
        {
            h.Append("<tr><td><code>").Append(E(t.Table)).Append("</code></td><td>").Append(t.Chunking == ChunkingMode.Range ? "key range" : "hash bucket")
             .Append("</td><td class=\"num\">").Append(N(t.Source.Rows)).Append("</td><td class=\"num\">").Append(N(t.Target.Rows))
             .Append("</td><td class=\"num\">").Append(N(t.TopLevelChunks)).Append("</td><td class=\"num\">").Append(N(t.MismatchedTopLevelChunks))
             .Append("</td>").Append(CountCell(t.Count(DiffKind.MissingInTarget))).Append(CountCell(t.Count(DiffKind.ExtraInTarget))).Append(CountCell(t.Count(DiffKind.Changed)))
             .Append("<td class=\"num\">").Append(N(t.Source.RowsTransferred + t.Target.RowsTransferred)).Append("</td><td class=\"num\">").Append(Ms(t.Timings.Total)).Append("</td></tr>\n");
        }

        h.Append("</tbody></table></div>\n");

        foreach (var t in tables.Where(t => t.Diffs.Count > 0))
        {
            h.Append("<h3><code>").Append(E(t.Table)).Append("</code>: ").Append(N(t.Diffs.Count)).Append(" differing rows")
             .Append(t.Truncated ? " (truncated)" : "").Append("</h3>\n");
            h.Append("<div class=\"scroll\"><table class=\"diffs\">\n<thead><tr><th>Key</th><th>Kind</th><th>Columns</th></tr></thead>\n<tbody>\n");
            foreach (var d in t.Diffs.Take(MaxRowsPerTable))
            {
                h.Append("<tr><td><code>").Append(E(d.DisplayKey)).Append("</code></td><td>").Append(Kind(d.Kind)).Append("</td><td>");
                foreach (var c in d.Columns)
                {
                    h.Append("<div class=\"col\"><code>").Append(E(c.Column)).Append("</code> <span class=\"tag\">").Append(E(c.Difference.ToString())).Append("</span> ")
                     .Append(E(c.Detail));
                    if (c.SourceValue is not null || c.TargetValue is not null)
                    {
                        h.Append("<br><span class=\"muted\">Oracle</span> <code>").Append(E(c.SourceValue ?? "")).Append("</code> <span class=\"muted\">PostgreSQL</span> <code>").Append(E(c.TargetValue ?? "")).Append("</code>");
                    }

                    h.Append("</div>");
                }

                h.Append("</td></tr>\n");
            }

            h.Append("</tbody></table></div>\n");
            if (t.Diffs.Count > MaxRowsPerTable)
            {
                h.Append("<p class=\"muted\">").Append(N(t.Diffs.Count - MaxRowsPerTable)).Append(" more rows in the JSON report.</p>\n");
            }
        }

        h.Append("</section>\n");
    }

    private static void RenderDetection(StringBuilder h, Scoring.DetectionScore d)
    {
        h.Append("<section id=\"detection\">\n<h2>Detection of injected defects</h2>\n")
         .Append("<p class=\"lede\">Synthetic run: defects were injected deliberately and recorded in a manifest. Detected = the exact row reported with the right kind; exact column = the injected column and no other; classified = the diagnosis matches.</p>\n")
         .Append("<div class=\"scroll\"><table>\n<thead><tr><th>Defect</th><th>Table</th><th class=\"num\">Injected</th><th class=\"num\">Detected</th><th class=\"num\">Exact column</th><th class=\"num\">Classified</th></tr></thead>\n<tbody>\n");
        foreach (var k in d.Kinds)
        {
            h.Append("<tr><td><code>").Append(E(k.Code)).Append("</code></td><td>").Append(E(k.Table)).Append("</td><td class=\"num\">").Append(N(k.Injected))
             .Append("</td><td class=\"num\">").Append(N(k.Detected)).Append("</td><td class=\"num\">").Append(N(k.ExactColumn)).Append("</td><td class=\"num\">").Append(N(k.ClassifiedAs)).Append("</td></tr>\n");
        }

        h.Append("<tr class=\"total\"><td>Total</td><td></td><td class=\"num\">").Append(N(d.Injected)).Append("</td><td class=\"num\">").Append(N(d.TruePositives))
         .Append("</td><td class=\"num\">").Append(N(d.ExactColumn)).Append("</td><td></td></tr>\n</tbody></table></div>\n")
         .Append("<p>Recall <strong>").Append(P(d.Recall)).Append("</strong> · precision <strong>").Append(P(d.Precision)).Append("</strong> · false positives ")
         .Append(N(d.FalsePositives)).Append(" · missed ").Append(N(d.FalseNegatives)).Append("</p>\n</section>\n");
    }

    private static void RenderAudit(StringBuilder h, AuditSection a)
    {
        var r = a.Result;
        h.Append("<section id=\"audit\">\n<h2>Dual-write audit</h2>\n")
         .Append("<p class=\"lede\">Repeated chunked comparisons of <code>").Append(E(r.Table)).Append("</code> while both databases were being written. A difference is confirmed when the same pair of row digests is seen in ")
         .Append(N(r.Options.ConfirmRounds)).Append(" consecutive rounds; differences that converge earlier are replication lag.</p>\n");
        if (a.Simulation is { } sim)
        {
            h.Append("<p>").Append(N(sim.Writes)).Append(" simulated writes in ").Append(Ms(sim.Elapsed)).Append(", ").Append(N(sim.Faults.Count)).Append(" injected faults.</p>\n");
        }

        if (a.Score is { } sc)
        {
            h.Append("<div class=\"pills\"><span class=\"pill\">recall ").Append(P(sc.Recall)).Append("</span><span class=\"pill\">precision ").Append(P(sc.Precision))
             .Append("</span><span class=\"pill\">median time to confirm ").Append(sc.MedianTimeToConfirm is { } m ? Ms(m) : "–")
             .Append("</span><span class=\"pill\">").Append(N(sc.Transients)).Append(" transient (lag) differences, median ").Append(sc.MedianTransient is { } mt ? Ms(mt) : "–").Append("</span></div>\n");
        }

        h.Append("<div class=\"scroll\"><table>\n<thead><tr><th>Key</th><th>Kind</th><th>Columns</th><th class=\"num\">Confirmed in round</th></tr></thead>\n<tbody>\n");
        foreach (var c in r.Confirmed)
        {
            h.Append("<tr><td><code>").Append(E(c.DisplayKey)).Append("</code></td><td>").Append(Kind(c.Kind)).Append("</td><td>")
             .Append(E(string.Join(", ", c.Columns.Select(x => $"{x.Column} ({x.Difference})")))).Append("</td><td class=\"num\">").Append(N(c.Round)).Append("</td></tr>\n");
        }

        h.Append("</tbody></table></div>\n");
        h.Append("<p class=\"muted\">").Append(N(r.Rounds.Count)).Append(" rounds, median round ").Append(Ms(Median(r.Rounds.Select(x => x.Elapsed)))).Append(", ")
         .Append(N(r.StillUnstable)).Append(" keys still changing at the end.</p>\n</section>\n");
    }

    private static void RenderMethod(StringBuilder h, RunInfo run)
    {
        h.Append("<section id=\"method\">\n<h2>Method</h2>\n<ul>\n")
         .Append("<li>Strategy: <strong>").Append(run.Strategy == CompareStrategy.DatabaseSide ? "database-side (pushdown)" : "client-side (stream and hash in .NET)")
         .Append("</strong>, about ").Append(N(run.ChunkRows)).Append(" rows per chunk, row digest ").Append(run.Canonical.Hash.ToString().ToUpperInvariant()).Append(".</li>\n")
         .Append("<li>Canonical encoding per field: <code>~</code> for NULL, otherwise <code>&lt;UTF-8 bytes&gt;:&lt;value&gt;</code>. Numbers without trailing zeros; DATE at seconds; TIMESTAMP at microseconds; TIMESTAMP WITH TIME ZONE as the UTC instant; CHAR with trailing blanks removed; CLOB as a digest of its UTF-8 text.</li>\n")
         .Append("<li>Empty strings on PostgreSQL: <strong>").Append(run.Canonical.EmptyStrings == Canonical.EmptyStringPolicy.Distinct ? "reported (Oracle cannot store '')" : "treated as NULL").Append("</strong>.</li>\n")
         .Append("<li>Each side is read in one read-only snapshot (Oracle SET TRANSACTION READ ONLY, PostgreSQL REPEATABLE READ READ ONLY). The two snapshots are not the same instant: compare a quiesced source, or use the dual-write audit.</li>\n")
         .Append("<li>MD5/SHA-256 here detect accidental differences. They are not a defence against someone who can write to both databases and wants to hide a change.</li>\n")
         .Append("</ul>\n</section>\n");
    }

    private static void Card(StringBuilder h, string tone, string value, string label) =>
        h.Append("<div class=\"card ").Append(tone).Append("\"><div class=\"value\">").Append(E(value)).Append("</div><div class=\"label\">").Append(E(label)).Append("</div></div>\n");

    private static string CountCell(int n) => n == 0 ? "<td class=\"num muted\">0</td>" : "<td class=\"num bad-text\">" + N(n) + "</td>";

    private static string Kind(DiffKind k) => k switch
    {
        DiffKind.MissingInTarget => "<span class=\"tag bad\">missing in PostgreSQL</span>",
        DiffKind.ExtraInTarget => "<span class=\"tag warn\">extra in PostgreSQL</span>",
        _ => "<span class=\"tag\">changed</span>",
    };

    private static string Badge(Severity s) => "<span class=\"badge " + Sev(s) + "\">" + s + "</span>";

    private static string Sev(Severity s) => s.ToString().ToLowerInvariant();

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string N(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    private static string P(double v) => v.ToString("0.0%", CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan t) => t.TotalSeconds >= 10
        ? t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : t.TotalMilliseconds.ToString("#,0", CultureInfo.InvariantCulture) + " ms";

    private static TimeSpan Median(IEnumerable<TimeSpan> values)
    {
        var list = values.Order().ToList();
        return list.Count == 0 ? TimeSpan.Zero : list[list.Count / 2];
    }

    private const string Css = """
        :root{--bg:#f7f7f5;--fg:#1c1c1a;--muted:#6b6b66;--line:#e2e1dc;--card:#fff;--ok:#1f7a4d;--bad:#b42318;--warn:#a15c07;--accent:#33415c;--code:#f0efea}
        @media (prefers-color-scheme:dark){:root{--bg:#141413;--fg:#ecebe6;--muted:#9c9b94;--line:#2c2c29;--card:#1c1c1a;--ok:#4cc38a;--bad:#f97066;--warn:#fdb022;--accent:#a9b8d6;--code:#262624}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:1180px;margin:0 auto;padding:32px 16px 64px}h1{font-size:2rem;margin:.2rem 0 .4rem;letter-spacing:-.01em}
        h2{font-size:1.3rem;margin:2.4rem 0 .5rem;padding-top:.6rem;border-top:1px solid var(--line)}h3{font-size:1.02rem;margin:1.6rem 0 .5rem}
        .eyebrow{color:var(--accent);font-weight:600;font-size:.82rem;text-transform:uppercase;letter-spacing:.06em;margin:0}.meta,.lede,.muted{color:var(--muted)}
        code{font:13px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace;background:var(--code);padding:1px 4px;border-radius:4px}
        .snippet{white-space:pre-wrap;word-break:break-word;display:inline-block}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin:24px 0}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px 16px}.card .value{font-size:1.6rem;font-weight:650;font-variant-numeric:tabular-nums}
        .card .label{color:var(--muted);font-size:.85rem}.card.ok .value{color:var(--ok)}.card.bad .value{color:var(--bad)}.card.warn .value{color:var(--warn)}
        nav{display:flex;flex-wrap:wrap;gap:6px 18px;margin:8px 0 0;font-size:.92rem}nav a,a{color:var(--accent)}
        .scroll{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--card)}
        table{border-collapse:collapse;width:100%;font-size:.9rem}th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top}
        th{font-weight:600;color:var(--muted);font-size:.8rem;text-transform:uppercase;letter-spacing:.03em;white-space:nowrap}tr:last-child td{border-bottom:0}
        .num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}.total td{font-weight:650}
        .badge,.tag,.pill{display:inline-block;border-radius:999px;font-size:.75rem;font-weight:600;padding:1px 8px;border:1px solid var(--line);white-space:nowrap}
        .badge.high,.tag.bad{color:var(--bad);border-color:currentColor}.badge.medium,.tag.warn{color:var(--warn);border-color:currentColor}
        .badge.low{color:var(--accent);border-color:currentColor}.badge.info{color:var(--muted)}.bad-text{color:var(--bad);font-weight:600}
        .pills{display:flex;flex-wrap:wrap;gap:8px;margin:10px 0 14px}.pill{font-size:.82rem;padding:3px 10px;background:var(--card)}
        .pill.high{color:var(--bad)}.pill.medium{color:var(--warn)}.pill.low{color:var(--accent)}.col+.col{margin-top:4px}
        """;
}
