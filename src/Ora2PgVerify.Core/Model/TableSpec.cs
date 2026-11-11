namespace Ora2PgVerify.Model;

/// <summary>One column that exists on both sides and is compared.</summary>
/// <param name="Name">Logical (lower-case) name, used in reports.</param>
/// <param name="OracleName">Name as stored in the Oracle dictionary (usually upper case).</param>
/// <param name="PostgresName">Name as stored in the PostgreSQL catalog (usually lower case).</param>
/// <param name="Kind">Comparison semantics.</param>
/// <param name="OracleType">Normalised Oracle type, e.g. <c>NUMBER(14,2)</c>, <c>DATE</c>.</param>
/// <param name="PostgresType">Normalised PostgreSQL type, e.g. <c>numeric(14,2)</c>, <c>timestamp(0) without time zone</c>.</param>
/// <param name="IntegerValued">True when the Oracle type cannot hold a fraction (NUMBER(p,0)); required for range chunking on a key.</param>
public sealed record ColumnPair(
    string Name,
    string OracleName,
    string PostgresName,
    CanonicalKind Kind,
    string OracleType,
    string PostgresType,
    bool IntegerValued = false)
{
    /// <summary>The PostgreSQL column is a <c>date</c> (no time of day).</summary>
    public bool PostgresIsDateOnly => PostgresType.Equals("date", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PostgreSQL column is binary floating point (real / double precision).</summary>
    public bool PostgresIsFloat =>
        PostgresType.StartsWith("double", StringComparison.OrdinalIgnoreCase) ||
        PostgresType.StartsWith("real", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PostgreSQL column carries a time zone (timestamptz).</summary>
    public bool PostgresHasTimeZone => PostgresType.Contains("with time zone", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How a table's key space is split into chunks.</summary>
public enum ChunkingMode
{
    /// <summary>Contiguous ranges of a single integer key: index range scans, and bisection of a bad chunk.</summary>
    Range,

    /// <summary>Buckets of a hash of the canonical key: works for text and composite keys, independent of collation.</summary>
    HashBucket,
}

/// <summary>A table pair to compare, with its key and the column pairs in a fixed order.</summary>
public sealed record TableSpec(
    string Name,
    string OracleSchema,
    string OracleTable,
    string PostgresSchema,
    string PostgresTable,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<ColumnPair> Columns)
{
    public ColumnPair Column(string name) =>
        Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Column '{name}' is not part of table spec '{Name}'.");

    public IReadOnlyList<ColumnPair> Keys => [.. KeyColumns.Select(Column)];

    /// <summary>Range chunking needs exactly one integer-valued key; everything else uses hash buckets.</summary>
    public ChunkingMode Chunking =>
        KeyColumns.Count == 1 && Column(KeyColumns[0]) is { Kind: CanonicalKind.Number, IntegerValued: true }
            ? ChunkingMode.Range
            : ChunkingMode.HashBucket;
}
