using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Compare;

public enum DiffKind
{
    /// <summary>The row exists in Oracle but not in PostgreSQL.</summary>
    MissingInTarget,

    /// <summary>The row exists in PostgreSQL but not in Oracle.</summary>
    ExtraInTarget,

    /// <summary>The row exists on both sides with different canonical content.</summary>
    Changed,
}

/// <summary>A best-effort diagnosis of why two canonical values differ, so a reviewer sees the pattern, not just "different".</summary>
public enum ValueDifference
{
    EmptyStringVsNull,
    NullVsValue,
    TrailingWhitespace,
    CaseOnly,
    TimeOfDayLost,
    TimeShift,
    NumericDrift,
    DigestDiffers,
    Other,
}

/// <summary>One differing column. Values are only present when the run opted in to <c>--include-values</c>.</summary>
public sealed record ColumnDiff(string Column, ValueDifference Difference, string Detail, string? SourceValue, string? TargetValue);

public sealed record RowDiff(string Key, string DisplayKey, DiffKind Kind, string? SourceDigest, string? TargetDigest, IReadOnlyList<ColumnDiff> Columns);

public sealed record PhaseTimings(TimeSpan Stats, TimeSpan Fingerprint, TimeSpan DrillDown, TimeSpan Details, TimeSpan Total);

public sealed record SideSummary(string Engine, long Rows, long Queries, long RowsTransferred);

public sealed record TableComparison(
    string Table,
    CompareStrategy Strategy,
    ChunkingMode Chunking,
    SideSummary Source,
    SideSummary Target,
    long ChunkRows,
    int TopLevelChunks,
    int MismatchedTopLevelChunks,
    int LeafChunks,
    IReadOnlyList<RowDiff> Diffs,
    bool Truncated,
    PhaseTimings Timings,
    CanonicalOptions Canonical)
{
    public bool IsEqual => Diffs.Count == 0;

    public int Count(DiffKind kind) => Diffs.Count(d => d.Kind == kind);
}

public sealed record CompareOptions
{
    public CompareStrategy Strategy { get; init; } = CompareStrategy.DatabaseSide;

    /// <summary>Target rows per top-level chunk.</summary>
    public long ChunkRows { get; init; } = 10_000;

    /// <summary>A mismatched range chunk with at most this many rows is fetched as row digests instead of split again.</summary>
    public long LeafRows { get; init; } = 1_000;

    /// <summary>Sub-chunks per split of a mismatched range chunk (data-diff's bisection factor).</summary>
    public int BisectionFactor { get; init; } = 16;

    /// <summary>Stop collecting row differences after this many (the rest of the run still finishes).</summary>
    public int MaxDiffs { get; init; } = 10_000;

    /// <summary>Ranges, buckets or keys bound per drill-down query (Oracle allows 1000 IN-list items).</summary>
    public int MaxBatch { get; init; } = 500;

    /// <summary>Put canonical values of differing columns into the result (and so into the report). Off by default.</summary>
    public bool IncludeValues { get; init; }

    public CanonicalOptions Canonical { get; init; } = CanonicalOptions.Default;
}
