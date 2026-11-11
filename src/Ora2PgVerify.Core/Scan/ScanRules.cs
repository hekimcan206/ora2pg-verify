using Ora2PgVerify.Model;

namespace Ora2PgVerify.Scan;

/// <summary>A token-pattern rule. <see cref="MatchAt"/> is tried at every token index of an object's source.</summary>
public sealed record ScanRule(
    string Id,
    string Title,
    Severity Severity,
    string Category,
    string Suggestion,
    string Reference,
    Func<IReadOnlyList<Token>, int, bool> MatchAt);

/// <summary>
/// The rule catalogue. Each rule names the construct, why it needs rework in PostgreSQL, and the usual rewrite.
/// The selection follows what ora2pg's migration assessment and the AWS SCT action items flag for PL/SQL
/// (see docs/adr/0004 for sources); severities are this project's judgement of manual effort.
/// </summary>
public static class ScanRules
{
    private const string PgDocs = "https://www.postgresql.org/docs/current/";
    private const string Ora2Pg = "https://ora2pg.darold.net/documentation.html";

    public static IReadOnlyList<ScanRule> All { get; } =
    [
        new("HIERARCHICAL_CONNECT_BY", "CONNECT BY hierarchical query", Severity.High, "SQL",
            "Rewrite as WITH RECURSIVE: START WITH becomes the anchor member, PRIOR the join condition; carry LEVEL as a depth column; NOCYCLE needs a visited-path array.",
            PgDocs + "queries-with.html", (t, i) => Seq(t, i, "CONNECT", "BY")),
        new("HIERARCHICAL_PATH", "SYS_CONNECT_BY_PATH / CONNECT_BY_ROOT", Severity.Medium, "SQL",
            "Accumulate the path (string or array) and the root in the recursive CTE.",
            PgDocs + "queries-with.html", (t, i) => Word(t, i, "SYS_CONNECT_BY_PATH") || Word(t, i, "CONNECT_BY_ROOT")),
        new("ROWNUM", "ROWNUM pseudo-column", Severity.Medium, "SQL",
            "Use LIMIT / FETCH FIRST n ROWS, or row_number() OVER (ORDER BY ...). ROWNUM is assigned before ORDER BY, so 'WHERE ROWNUM <= 10 ORDER BY x' is not a top-10.",
            PgDocs + "queries-limit.html", (t, i) => Word(t, i, "ROWNUM")),
        new("DECODE", "DECODE()", Severity.Low, "SQL",
            "Use CASE. DECODE treats NULL = NULL as a match; a CASE needs IS NOT DISTINCT FROM to keep that (or orafce's decode).",
            PgDocs + "functions-conditional.html", (t, i) => Call(t, i, "DECODE")),
        new("NVL", "NVL()", Severity.Low, "SQL",
            "Use COALESCE (evaluates lazily, Oracle's NVL evaluates both arguments). Mind empty-string semantics: NVL(x, 'n/a') never sees '' in Oracle.",
            PgDocs + "functions-conditional.html", (t, i) => Call(t, i, "NVL")),
        new("NVL2", "NVL2()", Severity.Low, "SQL",
            "Use CASE WHEN x IS NOT NULL THEN a ELSE b END.",
            PgDocs + "functions-conditional.html", (t, i) => Call(t, i, "NVL2")),
        new("OUTER_JOIN_PLUS", "(+) outer join", Severity.Medium, "SQL",
            "Rewrite as ANSI LEFT/RIGHT JOIN. Filters on the (+) side move into the ON clause or the join turns into an inner join.",
            PgDocs + "queries-table-expressions.html", (t, i) => Sym(t, i, "(") && Sym(t, i + 1, "+") && Sym(t, i + 2, ")")),
        new("AUTONOMOUS_TRANSACTION", "PRAGMA AUTONOMOUS_TRANSACTION", Severity.High, "Transactions",
            "PostgreSQL has no autonomous transactions. Options: dblink / pg_background loopback call, write the log from the application after commit, or a queue table processed separately.",
            Ora2Pg, (t, i) => Seq(t, i, "PRAGMA", "AUTONOMOUS_TRANSACTION")),
        new("SYSDATE", "SYSDATE", Severity.Medium, "Semantics",
            "LOCALTIMESTAMP(0) or now()::timestamp(0) return the transaction start time, SYSDATE the statement wall clock; use clock_timestamp() where that matters. SYSDATE follows the server OS time zone, PostgreSQL the session TimeZone.",
            PgDocs + "functions-datetime.html#FUNCTIONS-DATETIME-CURRENT", (t, i) => Word(t, i, "SYSDATE")),
        new("SYSTIMESTAMP", "SYSTIMESTAMP", Severity.Medium, "Semantics",
            "clock_timestamp() for the wall clock, current_timestamp for the transaction start (timestamptz in both cases).",
            PgDocs + "functions-datetime.html#FUNCTIONS-DATETIME-CURRENT", (t, i) => Word(t, i, "SYSTIMESTAMP")),
        new("EMPTY_STRING_LITERAL", "Empty string literal ''", Severity.Medium, "Semantics",
            "In Oracle '' is NULL: \"x = ''\" is never true and \"x := ''\" assigns NULL. In PostgreSQL '' is a value. Review the NULL logic; NULLIF(x, '') restores Oracle behaviour where needed.",
            "https://aws.amazon.com/blogs/database/handle-empty-strings-when-migrating-from-oracle-to-postgresql/", (t, i) => t[i] is { Kind: TokenKind.Literal, Text.Length: 0 }),
        new("MINUS", "MINUS set operator", Severity.Low, "SQL",
            "Use EXCEPT.", PgDocs + "queries-union.html", (t, i) => Word(t, i, "MINUS")),
        new("SEQUENCE_NEXTVAL", "seq.NEXTVAL / CURRVAL", Severity.Low, "SQL",
            "Use nextval('seq') / currval('seq'), or an identity column.",
            PgDocs + "functions-sequence.html", (t, i) => Sym(t, i, ".") && (Is(t, i + 1, "NEXTVAL") || Is(t, i + 1, "CURRVAL"))),
        new("FROM_DUAL", "SELECT ... FROM DUAL", Severity.Info, "SQL",
            "Drop FROM DUAL (PostgreSQL allows SELECT without FROM), or keep it with orafce's dual view.",
            Ora2Pg, (t, i) => Seq(t, i, "FROM", "DUAL")),
        new("MERGE", "MERGE statement", Severity.Low, "SQL",
            "PostgreSQL 15+ has MERGE, but concurrency behaviour differs; INSERT ... ON CONFLICT is the idiomatic upsert.",
            PgDocs + "sql-merge.html", (t, i) => Seq(t, i, "MERGE", "INTO")),
        new("BULK_COLLECT", "BULK COLLECT / FORALL", Severity.Medium, "PL/SQL",
            "Prefer one set-based statement; otherwise collect into arrays (array_agg) and use unnest() for the bulk DML.",
            PgDocs + "plpgsql-control-structures.html", (t, i) => Seq(t, i, "BULK", "COLLECT") || Word(t, i, "FORALL")),
        new("EXECUTE_IMMEDIATE", "EXECUTE IMMEDIATE", Severity.Low, "PL/SQL",
            "Use EXECUTE format('... %I ...', ident) USING binds; quote identifiers with %I, never concatenate.",
            PgDocs + "plpgsql-statements.html#PLPGSQL-STATEMENTS-EXECUTING-DYN", (t, i) => Seq(t, i, "EXECUTE", "IMMEDIATE")),
        new("DB_LINK_REFERENCE", "Database link reference (@dblink)", Severity.High, "Architecture",
            "Replace with postgres_fdw / oracle_fdw foreign tables, or move the cross-database call into the application.",
            PgDocs + "postgres-fdw.html", (t, i) => Sym(t, i, "@") && i > 0 && t[i - 1].Kind == TokenKind.Word && i + 1 < t.Count && t[i + 1].Kind == TokenKind.Word),
        new("LISTAGG", "LISTAGG()", Severity.Low, "SQL",
            "Use string_agg(x, sep ORDER BY ...).", PgDocs + "functions-aggregate.html", (t, i) => Call(t, i, "LISTAGG")),
        new("RAISE_APPLICATION_ERROR", "RAISE_APPLICATION_ERROR", Severity.Low, "PL/SQL",
            "Use RAISE EXCEPTION '...' USING ERRCODE = 'P0001' (map the -20xxx codes to SQLSTATEs callers can test).",
            PgDocs + "plpgsql-errors-and-messages.html", (t, i) => Call(t, i, "RAISE_APPLICATION_ERROR")),
        new("SQLCODE", "SQLCODE", Severity.Low, "PL/SQL",
            "Use SQLSTATE (five-character codes); error numbers do not carry over.",
            PgDocs + "plpgsql-control-structures.html#PLPGSQL-ERROR-TRAPPING", (t, i) => Word(t, i, "SQLCODE")),
        new("DATE_ARITHMETIC_FUNCS", "ADD_MONTHS / MONTHS_BETWEEN / LAST_DAY", Severity.Low, "SQL",
            "Use interval arithmetic (date + interval '1 month', age(), date_trunc) or orafce; month-end rounding rules differ.",
            PgDocs + "functions-datetime.html", (t, i) => Call(t, i, "ADD_MONTHS") || Call(t, i, "MONTHS_BETWEEN") || Call(t, i, "LAST_DAY")),
        new("INSTR", "INSTR()", Severity.Low, "SQL",
            "Use strpos() / position(); the 3- and 4-argument forms need orafce or a helper function.",
            PgDocs + "functions-string.html", (t, i) => Call(t, i, "INSTR")),
        new("TRANSACTION_CONTROL_IN_PLSQL", "COMMIT / ROLLBACK inside stored code", Severity.Medium, "Transactions",
            "Allowed only in PostgreSQL procedures invoked by CALL outside an explicit transaction block, never in functions or triggers.",
            PgDocs + "plpgsql-transactions.html", (t, i) => (Word(t, i, "COMMIT") || Word(t, i, "ROLLBACK")) && Sym(t, i + 1, ";")),
        new("OPTIMIZER_HINT", "Optimizer hint /*+ ... */", Severity.Info, "Performance",
            "PostgreSQL ignores hints (pg_hint_plan exists as an extension); check the plan instead.",
            PgDocs + "using-explain.html", (t, i) => t[i].Kind == TokenKind.Hint),
        new("TRIGGER_NEW_OLD", ":NEW / :OLD in trigger", Severity.Low, "PL/SQL",
            "Trigger bodies become a trigger function returning NEW/OLD; :NEW.col becomes NEW.col.",
            PgDocs + "plpgsql-trigger.html", (t, i) => Sym(t, i, ":") && (Word(t, i + 1, "NEW") || Word(t, i + 1, "OLD"))),
    ];

    /// <summary>Supplied packages and the effort to replace them.</summary>
    public static IReadOnlyDictionary<string, (Severity Severity, string Suggestion)> SuppliedPackages { get; } =
        new Dictionary<string, (Severity, string)>(StringComparer.Ordinal)
        {
            ["DBMS_OUTPUT"] = (Severity.Low, "RAISE NOTICE (client must listen for notices)."),
            ["DBMS_LOB"] = (Severity.Medium, "text/bytea functions (length, substr, position, overlay); orafce covers part of DBMS_LOB."),
            ["DBMS_SQL"] = (Severity.Medium, "EXECUTE format(...) with USING; cursors via refcursor."),
            ["DBMS_SCHEDULER"] = (Severity.High, "pg_cron, or an external scheduler; job metadata and logging must be rebuilt."),
            ["DBMS_JOB"] = (Severity.High, "pg_cron, or an external scheduler."),
            ["DBMS_CRYPTO"] = (Severity.Medium, "pgcrypto (digest, hmac, encrypt); algorithm names and padding differ."),
            ["DBMS_RANDOM"] = (Severity.Low, "random() / gen_random_uuid(); orafce has dbms_random."),
            ["DBMS_LOCK"] = (Severity.High, "pg_sleep for SLEEP; advisory locks (pg_advisory_lock) for user locks."),
            ["DBMS_ALERT"] = (Severity.High, "LISTEN / NOTIFY."),
            ["DBMS_PIPE"] = (Severity.High, "LISTEN / NOTIFY or a queue table; orafce has dbms_pipe."),
            ["DBMS_AQ"] = (Severity.High, "A queue table with SKIP LOCKED, or an external broker."),
            ["DBMS_UTILITY"] = (Severity.Medium, "Case by case (format_call_stack → GET DIAGNOSTICS PG_CONTEXT)."),
            ["DBMS_XMLGEN"] = (Severity.Medium, "query_to_xml / xmlagg."),
            ["UTL_FILE"] = (Severity.High, "No server-side file API in PL/pgSQL; COPY / pg_read_file need superuser-level grants. Move file I/O to the application."),
            ["UTL_HTTP"] = (Severity.High, "Move HTTP calls to the application (or the http extension)."),
            ["UTL_SMTP"] = (Severity.High, "Move mail to the application."),
            ["UTL_TCP"] = (Severity.High, "Move network I/O to the application."),
        };

    internal static bool Word(IReadOnlyList<Token> t, int i, string word) =>
        i < t.Count && t[i].Kind == TokenKind.Word && t[i].Text == word && !(i > 0 && Sym(t, i - 1, "."));

    internal static bool Is(IReadOnlyList<Token> t, int i, string word) =>
        i < t.Count && t[i].Kind == TokenKind.Word && t[i].Text == word;

    internal static bool Sym(IReadOnlyList<Token> t, int i, string symbol) =>
        i < t.Count && t[i].Kind == TokenKind.Symbol && t[i].Text == symbol;

    internal static bool Call(IReadOnlyList<Token> t, int i, string name) => Word(t, i, name) && Sym(t, i + 1, "(");

    internal static bool Seq(IReadOnlyList<Token> t, int i, params string[] words)
    {
        for (var k = 0; k < words.Length; k++)
        {
            if (i + k >= t.Count || t[i + k].Kind != TokenKind.Word || t[i + k].Text != words[k])
            {
                return false;
            }
        }

        return true;
    }
}
