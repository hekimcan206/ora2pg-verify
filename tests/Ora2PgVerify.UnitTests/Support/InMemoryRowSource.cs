using System.Globalization;
using System.Runtime.CompilerServices;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.UnitTests.Support;

/// <summary>
/// A row source over in-memory raw values that computes exactly what the SQL computes (same canonical encoding,
/// same chunk and bucket arithmetic), so the comparison algorithm can be tested exhaustively without a database.
/// </summary>
internal sealed class InMemoryRowSource(string engine, List<object?[]> rows, CanonicalOptions? options = null) : IRowSource
{
    private readonly CanonicalOptions _options = options ?? CanonicalOptions.Default;

    public string EngineName => engine;

    public List<object?[]> Rows => rows;

    public Task<IRowSourceSession> OpenSessionAsync(CancellationToken ct) =>
        Task.FromResult<IRowSourceSession>(new Session(engine, [.. rows], _options)); // snapshot copy, like a read-only transaction

    private sealed class Session(string engine, List<object?[]> rows, CanonicalOptions o) : IRowSourceSession
    {
        public string EngineName => engine;

        public SideMetrics Metrics { get; } = new();

        private string[] FieldsWith(TableSpec t, object?[] row) => [.. t.Columns.Select((c, i) => Canonicalizer.Field(c.Kind, row[i], o))];

        private string Digest(TableSpec t, object?[] row) => Canonicalizer.HexDigest(Canonicalizer.Row(FieldsWith(t, row)), o.Hash);

        private static long IntKey(object?[] row) => Convert.ToInt64(row[0], CultureInfo.InvariantCulture);

        private string Key(TableSpec t, object?[] row)
        {
            var f = FieldsWith(t, row);
            return t.Chunking == ChunkingMode.Range ? DifferenceClassifier.Decode(f[0])! : string.Concat(f.Take(t.KeyColumns.Count));
        }

        public Task<KeyStats> GetKeyStatsAsync(TableSpec table, CancellationToken ct)
        {
            Metrics.AddQuery(1);
            if (table.Chunking != ChunkingMode.Range || rows.Count == 0)
            {
                return Task.FromResult(new KeyStats(rows.Count, null, null));
            }

            return Task.FromResult(new KeyStats(rows.Count, rows.Min(IntKey), rows.Max(IntKey)));
        }

        public Task<IReadOnlyDictionary<long, ChunkHash>> GetRangeChunksAsync(TableSpec table, KeyRange range, long width, CancellationToken ct)
        {
            var result = rows.Where(r => IntKey(r) >= range.Lo && IntKey(r) <= range.Hi)
                .GroupBy(r => (IntKey(r) - range.Lo) / width)
                .ToDictionary(g => g.Key, g => new ChunkHash(g.Count(), g.Sum(r => (decimal)Canonicalizer.Hash60(Digest(table, r)))));
            Metrics.AddQuery(result.Count);
            return Task.FromResult<IReadOnlyDictionary<long, ChunkHash>>(result);
        }

        public Task<IReadOnlyDictionary<long, ChunkHash>> GetBucketChunksAsync(TableSpec table, long buckets, CancellationToken ct)
        {
            var result = rows.GroupBy(r => Canonicalizer.Bucket(Key(table, r), buckets))
                .ToDictionary(g => g.Key, g => new ChunkHash(g.Count(), g.Sum(r => (decimal)Canonicalizer.Hash60(Digest(table, r)))));
            Metrics.AddQuery(result.Count);
            return Task.FromResult<IReadOnlyDictionary<long, ChunkHash>>(result);
        }

        public Task<IReadOnlyList<RowHash>> GetRangeRowHashesAsync(TableSpec table, IReadOnlyList<KeyRange> ranges, CancellationToken ct)
        {
            var result = rows.Where(r => ranges.Any(x => IntKey(r) >= x.Lo && IntKey(r) <= x.Hi)).Select(r => new RowHash(Key(table, r), Digest(table, r))).ToList();
            Metrics.AddQuery(result.Count);
            return Task.FromResult<IReadOnlyList<RowHash>>(result);
        }

        public Task<IReadOnlyList<RowHash>> GetBucketRowHashesAsync(TableSpec table, long buckets, IReadOnlyList<long> bucketIds, CancellationToken ct)
        {
            var result = rows.Where(r => bucketIds.Contains(Canonicalizer.Bucket(Key(table, r), buckets))).Select(r => new RowHash(Key(table, r), Digest(table, r))).ToList();
            Metrics.AddQuery(result.Count);
            return Task.FromResult<IReadOnlyList<RowHash>>(result);
        }

        public Task<IReadOnlyList<RowFields>> GetRowFieldsAsync(TableSpec table, IReadOnlyList<string> keys, bool clientSide, CancellationToken ct)
        {
            var set = keys.ToHashSet(StringComparer.Ordinal);
            var result = rows.Where(r => set.Contains(Key(table, r))).Select(r => new RowFields(Key(table, r), FieldsWith(table, r))).ToList();
            Metrics.AddQuery(result.Count);
            return Task.FromResult<IReadOnlyList<RowFields>>(result);
        }

        public async IAsyncEnumerable<RowHash> StreamClientRowHashesAsync(TableSpec table, [EnumeratorCancellation] CancellationToken ct)
        {
            IEnumerable<object?[]> ordered = table.Chunking == ChunkingMode.Range ? rows.OrderBy(IntKey) : rows;
            foreach (var r in ordered)
            {
                await Task.Yield();
                yield return new RowHash(Key(table, r), Digest(table, r));
            }

            Metrics.AddQuery(rows.Count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
