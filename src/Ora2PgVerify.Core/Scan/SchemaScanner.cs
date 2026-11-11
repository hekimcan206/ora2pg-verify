using System.Globalization;
using System.Text.RegularExpressions;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Scan;

public sealed record ScanFinding(
    string RuleId,
    string Title,
    Severity Severity,
    string Category,
    string ObjectName,
    string ObjectType,
    int? Line,
    string? Snippet,
    string Suggestion,
    string Reference);

public sealed record ObjectSummary(string Name, string Type, string Status, int Lines, int Findings, int Points, Severity? Worst);

public sealed record ScanResult(string Schema, IReadOnlyList<ScanFinding> Findings, IReadOnlyList<ObjectSummary> Objects)
{
    public int Count(Severity s) => Findings.Count(f => f.Severity == s);

    public int TotalPoints => Objects.Sum(o => o.Points);
}

public sealed record ScanOptions
{
    /// <summary>Show string literal contents in snippets. Off by default: stored code can contain credentials or personal data.</summary>
    public bool ShowLiterals { get; init; }
}

/// <summary>
/// Flags Oracle constructs that need rework in PostgreSQL, from stored PL/SQL (token rules) and from the dictionary
/// (types, sequences, synonyms, links, temporary tables, materialized views).
/// </summary>
public static partial class SchemaScanner
{
    public static ScanResult Scan(SchemaFacts facts, ScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        options ??= new ScanOptions();
        var findings = new List<ScanFinding>();
        var lineCounts = new Dictionary<(string, string), (int Lines, string Status)>();

        foreach (var obj in facts.Sources.OrderBy(o => o.Type, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            var lines = obj.Text.Split('\n');
            lineCounts[(obj.Name, obj.Type)] = (lines.Length, obj.Status);
            findings.AddRange(ScanSource(obj, lines, options));
        }

        findings.AddRange(ScanCatalog(facts));

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.ObjectType, StringComparer.Ordinal)
            .ThenBy(f => f.ObjectName, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToList();

        var objects = ordered
            .GroupBy(f => (f.ObjectName, f.ObjectType))
            .Select(g =>
            {
                var (lines, status) = lineCounts.GetValueOrDefault(g.Key, (0, "VALID"));
                return new ObjectSummary(g.Key.ObjectName, g.Key.ObjectType, status, lines, g.Count(), g.Sum(f => f.Severity.Points()), g.Max(f => f.Severity));
            })
            .OrderByDescending(o => o.Points)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ThenBy(o => o.Type, StringComparer.Ordinal)
            .ToList();

        return new ScanResult(facts.Schema, ordered, objects);
    }

    public static IEnumerable<ScanFinding> ScanSource(SourceObject obj, string[] lines, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        var tokens = PlSqlLexer.Tokenize(obj.Text);
        var name = obj.Name;
        var type = obj.Type;
        string Snip(int line) => Snippet(lines, line, options);
        ScanFinding Make(string id, string title, Severity s, string cat, int? line, string suggestion, string reference) =>
            new(id, title, s, cat, name, type, line, line is { } l ? Snip(l) : null, suggestion, reference);

        for (var i = 0; i < tokens.Count; i++)
        {
            foreach (var rule in ScanRules.All)
            {
                if (rule.MatchAt(tokens, i))
                {
                    yield return Make(rule.Id, rule.Title, rule.Severity, rule.Category, tokens[i].Line, rule.Suggestion, rule.Reference);
                }
            }

            // Supplied packages: DBMS_xxx. / UTL_xxx. calls (with or without the SYS. prefix).
            if (tokens[i].Kind == TokenKind.Word && ScanRules.Sym(tokens, i + 1, ".") &&
                (tokens[i].Text.StartsWith("DBMS_", StringComparison.Ordinal) || tokens[i].Text.StartsWith("UTL_", StringComparison.Ordinal)))
            {
                var pkg = tokens[i].Text;
                var (sev, suggestion) = ScanRules.SuppliedPackages.TryGetValue(pkg, out var known)
                    ? known
                    : (Severity.Medium, "No direct equivalent; find the PostgreSQL feature or extension that covers this call.");
                yield return Make("SUPPLIED_PACKAGE", $"{pkg} call", sev, "Supplied packages", tokens[i].Line, suggestion,
                    "https://ora2pg.darold.net/documentation.html");
            }
        }

        if (type == "PACKAGE")
        {
            yield return Make("PACKAGE_STRUCTURE", "Package", Severity.Medium, "PL/SQL", null,
                "PostgreSQL has no packages: map to a schema plus functions/procedures (ora2pg PACKAGE_AS_SCHEMA); private routines lose their privacy, package initialisation blocks need an explicit call.",
                "https://ora2pg.darold.net/documentation.html");
        }

        if (type is "PACKAGE" or "PACKAGE BODY")
        {
            foreach (var (line, constant) in PackageLevelDeclarations(tokens, isSpec: type == "PACKAGE"))
            {
                yield return constant
                    ? Make("PACKAGE_CONSTANT", "Package-level constant", Severity.Low, "PL/SQL", line,
                        "Becomes an IMMUTABLE function or a row in a settings table.", "https://ora2pg.darold.net/documentation.html")
                    : Make("PACKAGE_STATE", "Package-level variable (session state)", Severity.High, "PL/SQL", line,
                        "Oracle keeps it per session for the life of the session. PostgreSQL options: set_config/current_setting (text, per session or transaction), a temporary table, or moving the state to the application. A transaction-mode connection pooler (PgBouncer) breaks any per-session state.",
                        "https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADMIN-SET");
            }
        }

        if (type == "TYPE")
        {
            yield return Make("OBJECT_TYPE", "Object type", Severity.High, "PL/SQL", null,
                "Composite types cover attributes only; member methods become functions taking the type.", "https://www.postgresql.org/docs/current/rowtypes.html");
        }

        if (!string.Equals(obj.Status, "VALID", StringComparison.OrdinalIgnoreCase))
        {
            yield return Make("INVALID_OBJECT", "Object is invalid in Oracle", Severity.Info, "Inventory", null,
                "Fix or drop it before migrating; the scan reads the stored source as is.", "https://ora2pg.darold.net/documentation.html");
        }
    }

    /// <summary>
    /// Declarations between the package header (IS/AS) and the first routine, cursor or initialisation block.
    /// A heuristic on tokens, so it is reported per declaration with its line for a human to confirm.
    /// </summary>
    internal static IEnumerable<(int Line, bool Constant)> PackageLevelDeclarations(IReadOnlyList<Token> t, bool isSpec)
    {
        var start = -1;
        for (var i = 0; i < t.Count; i++)
        {
            if (ScanRules.Is(t, i, "IS") || ScanRules.Is(t, i, "AS"))
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0)
        {
            yield break;
        }

        var statementStart = start;
        var skipStatement = false;
        for (var i = start; i < t.Count; i++)
        {
            var w = t[i].Kind == TokenKind.Word ? t[i].Text : null;
            if (i == statementStart)
            {
                // A spec lists routine signatures ("PROCEDURE p(x NUMBER);") between variables: skip them.
                // In a body the first routine starts the implementation section: stop.
                if (isSpec && w is "PROCEDURE" or "FUNCTION" or "CURSOR")
                {
                    skipStatement = true;
                }
                else if (w is "PROCEDURE" or "FUNCTION" or "CURSOR" or "BEGIN" or "END")
                {
                    yield break;
                }
            }

            if (!ScanRules.Sym(t, i, ";"))
            {
                continue;
            }

            if (skipStatement)
            {
                skipStatement = false;
                statementStart = i + 1;
                continue;
            }

            // statement = t[statementStart..i]
            if (i - statementStart >= 2 && t[statementStart].Kind is TokenKind.Word or TokenKind.QuotedIdentifier)
            {
                var first = t[statementStart].Text;
                var second = t[statementStart + 1];
                var skip = first is "TYPE" or "SUBTYPE" or "PRAGMA" ||
                           (second.Kind == TokenKind.Word && second.Text == "EXCEPTION");
                if (!skip)
                {
                    var constant = second.Kind == TokenKind.Word && second.Text == "CONSTANT";
                    yield return (t[statementStart].Line, constant);
                }
            }

            statementStart = i + 1;
        }
    }

    public static IEnumerable<ScanFinding> ScanCatalog(SchemaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string Ora2PgTypes = "https://ora2pg.darold.net/docs/configuration/type-control";
        foreach (var s in facts.Sequences.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (s.MaxValue > long.MaxValue)
            {
                yield return Catalog("SEQUENCE_MAXVALUE", "Sequence MAXVALUE beyond bigint", Severity.Medium, "Sequences", s.Name, "SEQUENCE",
                    string.Create(CultureInfo.InvariantCulture, $"MAXVALUE {s.MaxValue:0} exceeds 9223372036854775807; PostgreSQL sequences are bigint. Set an explicit MAXVALUE (and check the column type)."),
                    "https://www.postgresql.org/docs/current/sql-createsequence.html");
            }

            if (s.CacheSize > 1)
            {
                yield return Catalog("SEQUENCE_CACHE", "Sequence CACHE", Severity.Info, "Sequences", s.Name, "SEQUENCE",
                    "Both engines cache per session/instance, so values are not gap-free or ordered across sessions on either side; do not rely on it after migration either.",
                    "https://www.postgresql.org/docs/current/sql-createsequence.html");
            }
        }

        foreach (var syn in facts.Synonyms.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            yield return Catalog("SYNONYM", "Synonym", syn.DbLink is null ? Severity.Medium : Severity.High, "Name resolution", syn.Name, "SYNONYM",
                syn.DbLink is null
                    ? $"PostgreSQL has no synonyms. Use search_path, or a view over {syn.TargetOwner}.{syn.TargetName}."
                    : $"Synonym over a database link ({syn.DbLink}): needs a foreign table (postgres_fdw / oracle_fdw).",
                "https://www.postgresql.org/docs/current/ddl-schemas.html#DDL-SCHEMAS-PATH");
        }

        foreach (var link in facts.DbLinks.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            yield return Catalog("DB_LINK", "Database link", Severity.High, "Architecture", link.Name, "DATABASE LINK",
                "Replace with postgres_fdw / oracle_fdw, or move the integration into the application.", "https://www.postgresql.org/docs/current/postgres-fdw.html");
        }

        foreach (var t in facts.Tables.Where(t => t.Temporary).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            yield return Catalog("GLOBAL_TEMPORARY_TABLE", "Global temporary table", Severity.Medium, "Tables", t.Name, "TABLE",
                "PostgreSQL temporary tables are created per session (no shared definition); create them on demand or use the pgtt extension.",
                "https://www.postgresql.org/docs/current/sql-createtable.html");
        }

        foreach (var mv in facts.MaterializedViews.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            yield return Catalog("MATERIALIZED_VIEW", "Materialized view", Severity.Medium, "Tables", mv.Name, "MATERIALIZED VIEW",
                $"Refresh {mv.RefreshMethod} ON {mv.RefreshMode}: PostgreSQL only has full REFRESH MATERIALIZED VIEW [CONCURRENTLY], no fast or on-commit refresh.",
                "https://www.postgresql.org/docs/current/sql-refreshmaterializedview.html");
        }

        foreach (var c in facts.Columns.OrderBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal))
        {
            var obj = $"{c.Table}.{c.Column}";
            var type = c.DataType.ToUpperInvariant();
            var finding = type switch
            {
                "NUMBER" when c.Precision is null && c.Scale is null =>
                    ("TYPE_NUMBER_UNCONSTRAINED", "Unconstrained NUMBER", Severity.Low, "Choose bigint, numeric(p,s) or numeric from the actual data range; ora2pg's default depends on PG_INTEGER_TYPE / DEFAULT_NUMERIC, and double precision loses digits."),
                "DATE" => ("TYPE_DATE", "DATE column", Severity.Low, "Map to timestamp(0): Oracle DATE has a time of day, PostgreSQL date does not."),
                "LONG" or "LONG RAW" => ("TYPE_LONG", "LONG / LONG RAW column", Severity.High, "Deprecated in Oracle; map to text / bytea and rewrite code that reads it piecewise."),
                "BFILE" => ("TYPE_BFILE", "BFILE column", Severity.High, "External files are not stored in PostgreSQL; store a path and read in the application."),
                "ROWID" or "UROWID" => ("TYPE_ROWID", "ROWID column", Severity.Medium, "Physical row addresses do not carry over; store the primary key instead."),
                "XMLTYPE" => ("TYPE_XMLTYPE", "XMLTYPE column", Severity.Medium, "Map to xml; XPath functions and storage options differ."),
                "BINARY_FLOAT" or "BINARY_DOUBLE" => ("TYPE_BINARY_FLOAT", "BINARY_FLOAT / BINARY_DOUBLE column", Severity.Low, "Map to real / double precision; NaN and infinity literals differ."),
                _ when type.Contains("LOCAL TIME ZONE", StringComparison.Ordinal) =>
                    ("TYPE_TIMESTAMP_LTZ", "TIMESTAMP WITH LOCAL TIME ZONE column", Severity.Medium, "Map to timestamptz; display follows the session TimeZone instead of the Oracle session time zone."),
                "CHAR" or "NCHAR" => ("TYPE_CHAR", "CHAR column", Severity.Info, "Blank-padded in both engines, but PostgreSQL strips the padding when casting to text; comparisons with varchar behave differently."),
                _ => default((string, string, Severity, string)?),
            };
            if (finding is var (id, title, sev, suggestion))
            {
                yield return Catalog(id, title, sev, "Types", obj, "COLUMN", suggestion, Ora2PgTypes);
            }
        }
    }

    private static ScanFinding Catalog(string id, string title, Severity s, string category, string obj, string type, string suggestion, string reference) =>
        new(id, title, s, category, obj, type, null, null, suggestion, reference);

    [GeneratedRegex("'(?:[^']|'')+'")]
    private static partial Regex StringLiteral();

    private static string Snippet(string[] lines, int line, ScanOptions options)
    {
        var text = line >= 1 && line <= lines.Length ? lines[line - 1].Trim() : string.Empty;
        if (!options.ShowLiterals)
        {
            text = StringLiteral().Replace(text, "'…'");
        }

        return text.Length > 160 ? text[..157] + "..." : text;
    }
}
