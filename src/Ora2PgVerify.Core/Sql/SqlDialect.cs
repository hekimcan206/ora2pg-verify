using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Sql;

/// <summary>
/// Generates the pushdown SQL for one engine. Every query computes the canonical encoding of
/// <see cref="Canonicalizer"/> inside the database, so only fingerprints, digests or the rows of a bad chunk travel.
/// <para>
/// Identifiers come from the database catalogs, are validated against a strict pattern and quoted. Values are
/// always bound parameters. No user-supplied text is ever spliced into SQL.
/// </para>
/// </summary>
public abstract partial class SqlDialect
{
    public abstract string EngineName { get; }

    /// <summary>Bind-variable marker: ":" for Oracle, "@" for Npgsql.</summary>
    public abstract string ParameterPrefix { get; }

    public string P(string name) => ParameterPrefix + name;

    public abstract string SchemaOf(TableSpec t);

    public abstract string TableOf(TableSpec t);

    public abstract string NameOf(ColumnPair c);

    public string QualifiedTable(TableSpec t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return $"{Quote(SchemaOf(t))}.{Quote(TableOf(t))}";
    }

    public string Column(ColumnPair c) => Quote(NameOf(c));

    /// <summary>Canonical value expression (text or NULL) before the length prefix.</summary>
    public abstract string ValueExpression(ColumnPair c, CanonicalOptions o);

    /// <summary>Byte length of a text expression in UTF-8.</summary>
    protected abstract string ByteLength(string expr);

    /// <summary>Hex digest (lower case) of a text expression.</summary>
    public abstract string HexDigest(string textExpr, RowHashAlgorithm algorithm);

    /// <summary>First 60 bits of the digest as a non-negative integer.</summary>
    public abstract string Hash60(string textExpr, RowHashAlgorithm algorithm);

    /// <summary>Bucket number for hash-bucket chunking: first 32 bits of MD5(key) modulo the bound bucket count.</summary>
    public abstract string Bucket(string keyTextExpr, string modulusParameter);

    /// <summary>Chunk index of an integer key inside a range: floor((key - lo) / width).</summary>
    protected abstract string RangeIndex(string keyExpr, string lo, string width);

    /// <summary>Encoded field: '~' for NULL, otherwise 'byteLength:value'.</summary>
    public virtual string EncodedField(ColumnPair c, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(o);
        var v = ValueExpression(c, o);
        return $"CASE WHEN {v} IS NULL THEN '~' ELSE {ByteLength(v)} || ':' || {v} END";
    }

    public string RowString(TableSpec t, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(t);
        return string.Join(" || ", t.Columns.Select(c => EncodedField(c, o)));
    }

    /// <summary>Canonical key text (encoded key fields), used for hash buckets and for text/composite keys.</summary>
    public string KeyString(TableSpec t, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(t);
        return string.Join(" || ", t.Keys.Select(c => EncodedField(c, o)));
    }

    /// <summary>The expression rows are matched on: the integer key itself for range mode, the key text otherwise.</summary>
    public string MatchKey(TableSpec t, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Chunking == ChunkingMode.Range ? Column(t.Keys[0]) : KeyString(t, o);
    }

    // ---- queries ---------------------------------------------------------------------------------------

    /// <summary>Range mode: COUNT, MIN, MAX of the integer key. Hash mode: COUNT only (MIN/MAX are NULL).</summary>
    public string KeyStatsQuery(TableSpec t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Chunking == ChunkingMode.Range)
        {
            var k = Column(t.Keys[0]);
            return $"SELECT COUNT(*) AS cnt, MIN({k}) AS lo, MAX({k}) AS hi FROM {QualifiedTable(t)}";
        }

        return $"SELECT COUNT(*) AS cnt, NULL AS lo, NULL AS hi FROM {QualifiedTable(t)}";
    }

    /// <summary>One scan, one row per chunk: chunk index, row count, SUM of 60-bit row hashes. Parameters lo, hi, w.</summary>
    public string RangeChunkQuery(TableSpec t, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(o);
        var k = Column(t.Keys[0]);
        return $"SELECT b, COUNT(*) AS cnt, SUM(h) AS hsum FROM (SELECT {RangeIndex(k, P("lo"), P("w"))} AS b, {Hash60(RowString(t, o), o.Hash)} AS h " +
               $"FROM {QualifiedTable(t)} WHERE {k} BETWEEN {P("lo")} AND {P("hi")}) s GROUP BY b ORDER BY b";
    }

    /// <summary>One scan, one row per bucket. Parameter n (bucket count).</summary>
    public string BucketChunkQuery(TableSpec t, CanonicalOptions o)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(o);
        return $"SELECT b, COUNT(*) AS cnt, SUM(h) AS hsum FROM (SELECT {Bucket(KeyString(t, o), P("n"))} AS b, {Hash60(RowString(t, o), o.Hash)} AS h " +
               $"FROM {QualifiedTable(t)}) s GROUP BY b ORDER BY b";
    }

    /// <summary>Key and full row digest for every row in the given ranges. Parameters lo0..loN, hi0..hiN.</summary>
    public string RangeLeafQuery(TableSpec t, CanonicalOptions o, int ranges)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(o);
        var k = Column(t.Keys[0]);
        var where = string.Join(" OR ", Enumerable.Range(0, ranges).Select(i => Inv($"{k} BETWEEN {P("lo" + i)} AND {P("hi" + i)}")));
        return $"SELECT {k} AS k, {HexDigest(RowString(t, o), o.Hash)} AS d FROM {QualifiedTable(t)} WHERE {where}";
    }

    /// <summary>Key text and full row digest for every row in the given buckets. Parameters n, b0..bN.</summary>
    public string BucketLeafQuery(TableSpec t, CanonicalOptions o, int buckets)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(o);
        var keyText = KeyString(t, o);
        var list = string.Join(", ", Enumerable.Range(0, buckets).Select(i => P(Inv($"b{i}"))));
        return $"SELECT {keyText} AS k, {HexDigest(RowString(t, o), o.Hash)} AS d FROM {QualifiedTable(t)} WHERE {Bucket(keyText, P("n"))} IN ({list})";
    }

    /// <summary>Encoded fields of specific rows, for column-level diagnosis. Parameters k0..kN.</summary>
    public string FieldsByKeyQuery(TableSpec t, CanonicalOptions o, int keys)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(o);
        var match = MatchKey(t, o);
        var fields = string.Join(", ", t.Columns.Select((c, i) => Inv($"{EncodedField(c, o)} AS c{i}")));
        var list = string.Join(", ", Enumerable.Range(0, keys).Select(i => P(Inv($"k{i}"))));
        return $"SELECT {match} AS k, {fields} FROM {QualifiedTable(t)} WHERE {match} IN ({list})";
    }

    /// <summary>Raw column values for client-side canonicalisation; ordered by key in range mode (merge join).</summary>
    public string RawRowsQuery(TableSpec t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var cols = string.Join(", ", t.Columns.Select(Column));
        var order = t.Chunking == ChunkingMode.Range ? $" ORDER BY {Column(t.Keys[0])}" : string.Empty;
        return $"SELECT {cols} FROM {QualifiedTable(t)}{order}";
    }

    /// <summary>Raw column values of specific rows. Parameters k0..kN matched against <see cref="MatchKey"/>.</summary>
    public string RawRowsByKeyQuery(TableSpec t, CanonicalOptions o, int keys)
    {
        ArgumentNullException.ThrowIfNull(t);
        var cols = string.Join(", ", t.Columns.Select(Column));
        var list = string.Join(", ", Enumerable.Range(0, keys).Select(i => P(Inv($"k{i}"))));
        return $"SELECT {cols} FROM {QualifiedTable(t)} WHERE {MatchKey(t, o)} IN ({list})";
    }

    // ---- identifiers -----------------------------------------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#]{0,127}$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Quotes a catalog identifier after validating it; anything unusual is refused rather than escaped.</summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!IdentifierPattern().IsMatch(identifier))
        {
            throw new ArgumentException($"Refusing unusual identifier '{identifier}'.", nameof(identifier));
        }

        return "\"" + identifier + "\"";
    }

    protected static string Inv(FormattableString s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Largest possible encoded row in bytes, from the declared column types (Oracle SQL strings stop at 4000 bytes).</summary>
    public static int EstimateMaxRowBytes(IEnumerable<(ColumnPair Column, int? ByteLength)> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var total = 0;
        foreach (var (c, bytes) in columns)
        {
            var value = c.Kind switch
            {
                CanonicalKind.Number => 42,
                CanonicalKind.Date => 26,
                CanonicalKind.Timestamp => 26,
                CanonicalKind.TimestampTz => 27,
                CanonicalKind.LargeText => 64,
                _ => bytes ?? 4000,
            };
            total += value + Encoding.ASCII.GetByteCount(value.ToString(CultureInfo.InvariantCulture)) + 1;
        }

        return total;
    }
}
