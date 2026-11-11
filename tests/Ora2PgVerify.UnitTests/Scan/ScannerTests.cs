using Ora2PgVerify.Model;
using Ora2PgVerify.Scan;
using Ora2PgVerify.UnitTests.Support;

namespace Ora2PgVerify.UnitTests.Scan;

public sealed class ScannerTests
{
    private static IReadOnlyList<ScanFinding> ScanText(string type, string text, ScanOptions? options = null) =>
        [.. SchemaScanner.ScanSource(new SourceObject("APP", "OBJ", type, "VALID", text), text.Split('\n'), options ?? new ScanOptions())];

    private static IReadOnlyList<string> Rules(string type, string text) => [.. ScanText(type, text).Select(f => f.RuleId)];

    [Fact]
    public void Sample_schema_scan_matches_golden()
    {
        var facts = new SchemaFacts("APP_OWNER", SampleSchema.Objects(),
            [new SequenceFact("APP_OWNER", "ORDER_SEQ", 9999999999999999999999999999m, false, 20)],
            [new SynonymFact("APP_OWNER", "ORD", "APP_OWNER", "ORDERS", null)],
            [],
            [new ColumnFact("APP_OWNER", "ORDERS", "CREATED_ON", "DATE", null, null), new ColumnFact("APP_OWNER", "ORDERS", "CUSTOMER_CODE", "CHAR", null, null)],
            [new TableFact("APP_OWNER", "TMP_ORDER_IDS", true)],
            []);
        var result = SchemaScanner.Scan(facts);

        Golden.Verify("scan-sample.txt", SampleSchema.Describe(result.Findings));
        result.Objects[0].Name.Should().Be("BILLING_PKG", "the package body carries the most rework");
        result.Count(Severity.High).Should().BeGreaterThan(5);
    }

    [Fact]
    public void Constructs_inside_comments_and_literals_are_not_findings()
    {
        var text = """
            BEGIN
              -- SELECT * FROM t CONNECT BY PRIOR id = parent_id; NVL(x, 1) ROWNUM
              /* DECODE(a, b) and SYSDATE */
              v := 'CONNECT BY ROWNUM DECODE(';
              v := q'[it's NVL( SYSDATE ]';
            END;
            """;
        Rules("PROCEDURE", text).Should().BeEmpty();
    }

    [Fact]
    public void Each_rule_fires_on_its_construct()
    {
        Rules("VIEW", "SELECT LEVEL FROM dual CONNECT BY LEVEL <= 3").Should().Contain(["HIERARCHICAL_CONNECT_BY", "FROM_DUAL"]);
        Rules("FUNCTION", "x := NVL(a, b) + NVL2(a, 1, 2) + DECODE(a, 1, 2);").Should().Contain(["NVL", "NVL2", "DECODE"]);
        Rules("VIEW", "SELECT * FROM a, b WHERE a.id = b.id(+) AND ROWNUM < 5").Should().Contain(["OUTER_JOIN_PLUS", "ROWNUM"]);
        Rules("PROCEDURE", "PRAGMA AUTONOMOUS_TRANSACTION;").Should().Equal("AUTONOMOUS_TRANSACTION");
        Rules("PROCEDURE", "IF v = '' THEN NULL; END IF;").Should().Contain("EMPTY_STRING_LITERAL");
        Rules("PROCEDURE", "SELECT seq.NEXTVAL INTO v FROM dual;").Should().Contain("SEQUENCE_NEXTVAL");
        Rules("PROCEDURE", "DELETE FROM t@remote_link;").Should().Contain("DB_LINK_REFERENCE");
        Rules("PROCEDURE", "SELECT /*+ FULL(t) */ * FROM t;").Should().Contain("OPTIMIZER_HINT");
        Rules("TRIGGER", ":NEW.x := :OLD.x;").Should().Equal("TRIGGER_NEW_OLD", "TRIGGER_NEW_OLD");
    }

    [Fact]
    public void Supplied_packages_are_rated_individually()
    {
        var findings = ScanText("PROCEDURE", "DBMS_OUTPUT.PUT_LINE('x'); SYS.DBMS_SCHEDULER.RUN_JOB('j'); DBMS_FOO.BAR; UTL_FILE.FOPEN('d','f','r');");
        findings.Where(f => f.RuleId == "SUPPLIED_PACKAGE").Select(f => (f.Title, f.Severity)).Should().Equal(
            ("DBMS_OUTPUT call", Severity.Low), ("DBMS_SCHEDULER call", Severity.High), ("DBMS_FOO call", Severity.Medium), ("UTL_FILE call", Severity.High));
    }

    [Fact]
    public void Package_state_is_found_per_declaration_and_constants_are_told_apart()
    {
        var spec = """
            PACKAGE P AS
              g_counter PLS_INTEGER := 0;
              c_limit CONSTANT NUMBER := 10;
              e_fail EXCEPTION;
              TYPE t_list IS TABLE OF NUMBER;
              PROCEDURE run(p IN NUMBER);
              g_after VARCHAR2(10);
            END P;
            """;
        var findings = ScanText("PACKAGE", spec);
        findings.Where(f => f.RuleId == "PACKAGE_STATE").Select(f => f.Line).Should().Equal(2, 7);
        findings.Where(f => f.RuleId == "PACKAGE_CONSTANT").Select(f => f.Line).Should().Equal(3);

        var body = "PACKAGE BODY P AS\n  g_hits NUMBER;\n  PROCEDURE run(p IN NUMBER) IS v NUMBER; BEGIN NULL; END;\nEND P;";
        ScanText("PACKAGE BODY", body).Where(f => f.RuleId == "PACKAGE_STATE").Select(f => f.Line).Should().Equal(2);
    }

    [Fact]
    public void Literals_are_redacted_in_snippets_unless_asked()
    {
        var text = "v_pw := 'S3cret!'; x := NVL(v_pw, '');";
        ScanText("PROCEDURE", text).First(f => f.RuleId == "NVL").Snippet.Should().Be("v_pw := '…'; x := NVL(v_pw, '');");
        ScanText("PROCEDURE", text, new ScanOptions { ShowLiterals = true }).First(f => f.RuleId == "NVL").Snippet.Should().Contain("S3cret!");
    }

    [Fact]
    public void Lexer_tracks_lines_across_comments_and_multiline_literals()
    {
        var tokens = PlSqlLexer.Tokenize("a\n/* x\ny */ b 'c\nd' e\n-- z\nf");
        tokens.Select(t => (t.Text, t.Line)).Should().Equal(("A", 1), ("B", 3), ("c\nd", 3), ("E", 4), ("F", 6));
    }

    [Fact]
    public void Catalog_facts_produce_findings()
    {
        var facts = new SchemaFacts("S", [],
            [new SequenceFact("S", "SMALL_SEQ", 1_000_000m, false, 1)],
            [new SynonymFact("S", "REMOTE_T", "X", "T", "LINK1")],
            [new DbLinkFact("S", "LINK1")],
            [new ColumnFact("S", "T", "N", "NUMBER", null, null), new ColumnFact("S", "T", "L", "LONG", null, null), new ColumnFact("S", "T", "Z", "TIMESTAMP(6) WITH LOCAL TIME ZONE", null, 6)],
            [],
            [new MaterializedViewFact("S", "MV", "FAST", "COMMIT")]);
        SchemaScanner.Scan(facts).Findings.Select(f => f.RuleId).Should().BeEquivalentTo(
            ["SYNONYM", "DB_LINK", "TYPE_NUMBER_UNCONSTRAINED", "TYPE_LONG", "TYPE_TIMESTAMP_LTZ", "MATERIALIZED_VIEW"]);
    }
}
