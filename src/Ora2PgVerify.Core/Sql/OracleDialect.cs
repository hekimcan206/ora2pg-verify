using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Sql;

/// <summary>
/// Oracle side of the canonical encoding (tested on Oracle Database 23ai Free, AL32UTF8).
/// <list type="bullet">
/// <item>Row digests: <c>STANDARD_HASH(.., 'MD5' | 'SHA256')</c>, which hashes the bytes of the database character set.
/// It refuses LOBs (ORA-00902), so CLOB columns are digested with <c>DBMS_CRYPTO.HASH</c>, which needs EXECUTE on
/// SYS.DBMS_CRYPTO and converts the CLOB to AL32UTF8 before hashing, matching PostgreSQL's <c>md5(text)</c>.</item>
/// <item>Numbers: <c>TO_CHAR(n, 'TM9')</c> is the shortest text form, but writes 0.5 as ".5"; the leading zero is restored.
/// The NLS argument pins the decimal separator whatever the session says.</item>
/// <item>Row strings are VARCHAR2 and stop at 4000 bytes (MAX_STRING_SIZE=STANDARD); wide text travels as a digest.</item>
/// </list>
/// </summary>
public sealed class OracleDialect : SqlDialect
{
    public static OracleDialect Instance { get; } = new();

    public override string EngineName => "Oracle";

    public override string ParameterPrefix => ":";

    public override string SchemaOf(TableSpec t) => t.OracleSchema;

    public override string TableOf(TableSpec t) => t.OracleTable;

    public override string NameOf(ColumnPair c) => c.OracleName;

    public override string ValueExpression(ColumnPair c, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(o);
        var col = Column(c);
        return c.Kind switch
        {
            CanonicalKind.Number => $"REGEXP_REPLACE(TO_CHAR({col}, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,'''), '^(-?)\\.', '\\10.')",
            CanonicalKind.Date => $"TO_CHAR({col}, 'YYYY-MM-DD\"T\"HH24:MI:SS')",
            CanonicalKind.Timestamp => $"TO_CHAR({col}, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6')",
            CanonicalKind.TimestampTz => $"TO_CHAR(SYS_EXTRACT_UTC({col}), 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6\"Z\"')",
            // Oracle has no empty string: '' is NULL already, so both policies read the column as is.
            CanonicalKind.Text => col,
            // RTRIM of an all-blank CHAR is '' which Oracle turns into NULL: blank-padded semantics end to end.
            CanonicalKind.FixedChar => $"RTRIM({col})",
            CanonicalKind.LargeText => Digest(c, o),
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

        // Fixed-length digest: no need to evaluate DBMS_CRYPTO twice for the length prefix.
        return $"CASE WHEN {NullTest(c, o)} THEN '~' ELSE '{o.DigestHexLength}:' || {Digest(c, o)} END";
    }

    private string NullTest(ColumnPair c, CanonicalOptions o)
    {
        var col = Column(c);
        var isLob = IsLob(c);
        return o.EmptyStrings == EmptyStringPolicy.AsNull && isLob ? $"({col} IS NULL OR DBMS_LOB.GETLENGTH({col}) = 0)" : $"{col} IS NULL";
    }

    private static bool IsLob(ColumnPair c) => c.OracleType.EndsWith("CLOB", StringComparison.OrdinalIgnoreCase);

    private string Digest(ColumnPair c, CanonicalOptions o)
    {
        var col = Column(c);
        if (IsLob(c))
        {
            var algorithm = o.Hash == RowHashAlgorithm.Md5 ? 2 : 4; // DBMS_CRYPTO.HASH_MD5 / HASH_SH256
            return Inv($"LOWER(RAWTOHEX(SYS.DBMS_CRYPTO.HASH({col}, {algorithm})))");
        }

        return HexDigest(col, o.Hash);
    }

    protected override string ByteLength(string expr) => $"LENGTHB({expr})";

    public override string HexDigest(string textExpr, RowHashAlgorithm algorithm) =>
        $"LOWER(RAWTOHEX(STANDARD_HASH({textExpr}, '{Algorithm(algorithm)}')))";

    public override string Hash60(string textExpr, RowHashAlgorithm algorithm) =>
        $"TO_NUMBER(SUBSTR(RAWTOHEX(STANDARD_HASH({textExpr}, '{Algorithm(algorithm)}')), 1, 15), 'XXXXXXXXXXXXXXX')";

    public override string Bucket(string keyTextExpr, string modulusParameter) =>
        $"MOD(TO_NUMBER(SUBSTR(RAWTOHEX(STANDARD_HASH({keyTextExpr}, 'MD5')), 1, 8), 'XXXXXXXX'), {modulusParameter})";

    protected override string RangeIndex(string keyExpr, string lo, string width) => $"FLOOR(({keyExpr} - {lo}) / {width})";

    private static string Algorithm(RowHashAlgorithm a) => a == RowHashAlgorithm.Md5 ? "MD5" : "SHA256";
}
