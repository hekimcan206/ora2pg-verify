using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Sql;

/// <summary>
/// PostgreSQL side of the canonical encoding (PostgreSQL 13+ for <c>trim_scale</c>, 11+ for <c>sha256</c>; no extension needed).
/// <list type="bullet">
/// <item>Numbers: <c>trim_scale(x::numeric)::text</c>. Binary floats go through numeric, which keeps 15 significant
/// digits, so a NUMBER mapped to double precision shows its lost digits instead of hiding them.</item>
/// <item>Time: rendered in UTC with microseconds; a fraction on a column that mirrors an Oracle DATE is kept so it is reported.</item>
/// <item>Text: <c>md5(text)</c> and <c>octet_length</c> work on the UTF-8 bytes of the database encoding.</item>
/// </list>
/// </summary>
public sealed class PostgresDialect : SqlDialect
{
    public static PostgresDialect Instance { get; } = new();

    public override string EngineName => "PostgreSQL";

    public override string ParameterPrefix => "@";

    public override string SchemaOf(TableSpec t) => t.PostgresSchema;

    public override string TableOf(TableSpec t) => t.PostgresTable;

    public override string NameOf(ColumnPair c) => c.PostgresName;

    public override string ValueExpression(ColumnPair c, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(o);
        var col = Column(c);
        var ts = c.PostgresHasTimeZone ? $"({col} AT TIME ZONE 'UTC')" : c.PostgresIsDateOnly ? $"{col}::timestamp" : col;
        return c.Kind switch
        {
            CanonicalKind.Number => $"trim_scale({col}::numeric)::text",
            CanonicalKind.Date => $"to_char({ts}, 'YYYY-MM-DD\"T\"HH24:MI:SS') || CASE WHEN date_trunc('second', {ts}) <> {ts} THEN to_char({ts}, '.US') ELSE '' END",
            CanonicalKind.Timestamp => $"to_char({ts}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US')",
            CanonicalKind.TimestampTz => $"to_char({ts}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')",
            CanonicalKind.Text => o.EmptyStrings == EmptyStringPolicy.AsNull ? $"NULLIF({col}::text, '')" : $"{col}::text",
            CanonicalKind.FixedChar => $"NULLIF(rtrim({col}::text, ' '), '')",
            CanonicalKind.LargeText => HexDigest(col, o.Hash),
            _ => throw new ArgumentOutOfRangeException(nameof(c), c.Kind, null),
        };
    }

    public override string EncodedField(ColumnPair c, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(o);
        if (c.Kind != CanonicalKind.LargeText)
        {
            return base.EncodedField(c, o);
        }

        var col = Column(c);
        var nullTest = o.EmptyStrings == EmptyStringPolicy.AsNull ? $"({col} IS NULL OR {col}::text = '')" : $"{col} IS NULL";
        return $"CASE WHEN {nullTest} THEN '~' ELSE '{o.DigestHexLength}:' || {HexDigest($"{col}::text", o.Hash)} END";
    }

    protected override string ByteLength(string expr) => $"octet_length({expr})::text";

    public override string HexDigest(string textExpr, RowHashAlgorithm algorithm) =>
        algorithm == RowHashAlgorithm.Md5 ? $"md5({textExpr})" : $"encode(sha256(convert_to({textExpr}, 'UTF8')), 'hex')";

    public override string Hash60(string textExpr, RowHashAlgorithm algorithm) =>
        $"('x' || substr({HexDigest(textExpr, algorithm)}, 1, 15))::bit(60)::bigint";

    public override string Bucket(string keyTextExpr, string modulusParameter) =>
        $"mod(('x' || substr(md5({keyTextExpr}), 1, 8))::bit(32)::bigint, {modulusParameter})";

    protected override string RangeIndex(string keyExpr, string lo, string width) => $"div(({keyExpr} - {lo})::numeric, {width})::bigint";
}
