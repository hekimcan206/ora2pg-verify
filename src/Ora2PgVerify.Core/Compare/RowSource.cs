using Ora2PgVerify.Model;

namespace Ora2PgVerify.Compare;

/// <summary>Row count and integer key bounds of one side (bounds are null in hash-bucket mode or for an empty table).</summary>
public sealed record KeyStats(long Count, long? Min, long? Max);

/// <summary>Fingerprint of one chunk: row count and SUM of 60-bit row hashes.</summary>
public readonly record struct ChunkHash(long Count, decimal Sum);

/// <summary>Key and full row digest of one row. <paramref name="Key"/> is canonical (an integer as text, or encoded key fields).</summary>
public readonly record struct RowHash(string Key, string Digest);

/// <summary>Encoded field values of one row, in <see cref="TableSpec.Columns"/> order.</summary>
public sealed record RowFields(string Key, IReadOnlyList<string> Fields);

/// <summary>An inclusive integer key range.</summary>
public readonly record struct KeyRange(long Lo, long Hi)
{
    public long Width => Hi - Lo + 1;
}

/// <summary>Where row canonicalisation happens.</summary>
public enum CompareStrategy
{
    /// <summary>Canonical encoding and hashing in SQL on each engine; only fingerprints and bad-chunk digests travel.</summary>
    DatabaseSide,

    /// <summary>Every row is streamed to the client and canonicalised in C#: the baseline, and a cross-check of the SQL.</summary>
    ClientSide,
}

/// <summary>Counters for one side of one comparison, to make pushdown savings measurable.</summary>
public sealed class SideMetrics
{
    private long _queries;
    private long _rowsTransferred;
    private long _busyTicks;

    public long Queries => Interlocked.Read(ref _queries);

    /// <summary>Result rows that crossed the network (fingerprint rows, digests, field rows or raw rows).</summary>
    public long RowsTransferred => Interlocked.Read(ref _rowsTransferred);

    /// <summary>Time spent waiting on this engine (execute and fetch), summed over queries.</summary>
    public TimeSpan Busy => TimeSpan.FromTicks(Interlocked.Read(ref _busyTicks));

    public void AddQuery(long rows, TimeSpan elapsed = default)
    {
        Interlocked.Increment(ref _queries);
        Interlocked.Add(ref _rowsTransferred, rows);
        Interlocked.Add(ref _busyTicks, elapsed.Ticks);
    }
}

/// <summary>
/// One open, read-only session on one engine. Implementations hold a single connection in a snapshot transaction
/// (Oracle <c>SET TRANSACTION READ ONLY</c>, PostgreSQL <c>REPEATABLE READ READ ONLY</c>) so that the fingerprint
/// pass and the drill-down see the same data.
/// </summary>
public interface IRowSourceSession : IAsyncDisposable
{
    string EngineName { get; }

    SideMetrics Metrics { get; }

    Task<KeyStats> GetKeyStatsAsync(TableSpec table, CancellationToken ct);

    /// <summary>Range mode: chunk index → fingerprint, for chunks of <paramref name="width"/> keys starting at <paramref name="range"/>.Lo.</summary>
    Task<IReadOnlyDictionary<long, ChunkHash>> GetRangeChunksAsync(TableSpec table, KeyRange range, long width, CancellationToken ct);

    /// <summary>Hash-bucket mode: bucket → fingerprint.</summary>
    Task<IReadOnlyDictionary<long, ChunkHash>> GetBucketChunksAsync(TableSpec table, long buckets, CancellationToken ct);

    Task<IReadOnlyList<RowHash>> GetRangeRowHashesAsync(TableSpec table, IReadOnlyList<KeyRange> ranges, CancellationToken ct);

    Task<IReadOnlyList<RowHash>> GetBucketRowHashesAsync(TableSpec table, long buckets, IReadOnlyList<long> bucketIds, CancellationToken ct);

    /// <summary>Encoded fields of the given rows. <paramref name="clientSide"/> reads raw values and canonicalises in C#.</summary>
    Task<IReadOnlyList<RowFields>> GetRowFieldsAsync(TableSpec table, IReadOnlyList<string> keys, bool clientSide, CancellationToken ct);

    /// <summary>All rows, canonicalised and hashed in C#. Ordered by key in range mode.</summary>
    IAsyncEnumerable<RowHash> StreamClientRowHashesAsync(TableSpec table, CancellationToken ct);
}

/// <summary>Opens sessions on one engine.</summary>
public interface IRowSource
{
    string EngineName { get; }

    Task<IRowSourceSession> OpenSessionAsync(CancellationToken ct);
}
