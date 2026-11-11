using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Compare;

/// <summary>
/// Compares one table across two engines.
/// <para>
/// Database-side strategy: (1) one grouped scan per side returns COUNT and SUM(hash60) per chunk; (2) a range chunk
/// whose fingerprints differ is split <see cref="CompareOptions.BisectionFactor"/> ways and re-fingerprinted until it
/// holds at most <see cref="CompareOptions.LeafRows"/> rows; (3) leaf chunks return key + row digest, which locates the
/// exact rows; (4) only those rows are fetched field by field to name the differing columns.
/// </para>
/// <para>Client-side strategy: every row is streamed and canonicalised in C#, then merge-joined (range) or hash-joined (buckets).</para>
/// </summary>
public sealed class TableComparer(CompareOptions options)
{
    private readonly CompareOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<TableComparison> CompareAsync(TableSpec table, IRowSource source, IRowSource target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        // A read-only snapshot taken within seconds of DDL on the table (CREATE, TRUNCATE) is refused by Oracle
        // (ORA-01466); a fresh snapshot a moment later succeeds. Anything else propagates unchanged.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CompareOnceAsync(table, source, target, ct);
            }
            catch (Exception ex) when (attempt < 4 && (source.IsRetryableSnapshotError(ex) || target.IsRetryableSnapshotError(ex)))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    private async Task<TableComparison> CompareOnceAsync(TableSpec table, IRowSource source, IRowSource target, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var sessions = await Task.WhenAll(source.OpenSessionAsync(ct), target.OpenSessionAsync(ct));
        await using var s = sessions[0];
        await using var t = sessions[1];

        var sw = Stopwatch.StartNew();
        var (sStats, tStats) = await Both(s, t, x => x.GetKeyStatsAsync(table, ct));
        var statsTime = sw.Elapsed;

        var run = new Run(table, _options, s, t, sStats, tStats);
        if (_options.Strategy == CompareStrategy.ClientSide)
        {
            await run.ClientSideAsync(ct);
        }
        else if (table.Chunking == ChunkingMode.Range)
        {
            await run.RangeAsync(ct);
        }
        else
        {
            await run.BucketsAsync(ct);
        }

        await run.DetailsAsync(ct);

        return new TableComparison(
            table.Name,
            _options.Strategy,
            table.Chunking,
            new SideSummary(s.EngineName, sStats.Count, s.Metrics.Queries, s.Metrics.RowsTransferred, s.Metrics.Busy),
            new SideSummary(t.EngineName, tStats.Count, t.Metrics.Queries, t.Metrics.RowsTransferred, t.Metrics.Busy),
            _options.ChunkRows,
            run.TopLevelChunks,
            run.MismatchedTopLevel,
            run.LeafChunks,
            run.Diffs,
            run.Truncated,
            new PhaseTimings(statsTime, run.FingerprintTime, run.DrillDownTime, run.DetailsTime, total.Elapsed),
            _options.Canonical);
    }

    internal static async Task<(T Source, T Target)> Both<T>(IRowSourceSession s, IRowSourceSession t, Func<IRowSourceSession, Task<T>> query)
    {
        var a = query(s);
        var b = query(t);
        await Task.WhenAll(a, b);
        return (await a, await b);
    }

    private sealed class Run(TableSpec table, CompareOptions o, IRowSourceSession s, IRowSourceSession t, KeyStats sStats, KeyStats tStats)
    {
        private readonly List<RowDiff> _diffs = [];

        public IReadOnlyList<RowDiff> Diffs => _diffs;

        public bool Truncated { get; private set; }

        public int TopLevelChunks { get; private set; }

        public int MismatchedTopLevel { get; private set; }

        public int LeafChunks { get; private set; }

        public TimeSpan FingerprintTime { get; private set; }

        public TimeSpan DrillDownTime { get; private set; }

        public TimeSpan DetailsTime { get; private set; }

        private long MaxCount => Math.Max(sStats.Count, tStats.Count);

        public async Task RangeAsync(CancellationToken ct)
        {
            if (MaxCount == 0)
            {
                return;
            }

            var lo = Min(sStats.Min, tStats.Min);
            var hi = Max(sStats.Max, tStats.Max);
            var root = new KeyRange(lo, hi);
            // Key width that holds about ChunkRows rows if keys are spread evenly between MIN and MAX.
            var width = Math.Max(1, (long)Math.Ceiling(root.Width * (double)o.ChunkRows / MaxCount));

            var sw = Stopwatch.StartNew();
            var leaves = new List<KeyRange>();
            var pending = new List<(KeyRange Range, long Width)> { (root, width) };
            var topLevel = true;
            while (pending.Count > 0)
            {
                var next = new List<(KeyRange, long)>();
                foreach (var (range, w) in pending)
                {
                    var (sc, tc) = await Both(s, t, x => x.GetRangeChunksAsync(table, range, w, ct));
                    if (topLevel)
                    {
                        TopLevelChunks = sc.Keys.Union(tc.Keys).Count();
                    }

                    foreach (var idx in sc.Keys.Union(tc.Keys).Order())
                    {
                        sc.TryGetValue(idx, out var a);
                        tc.TryGetValue(idx, out var b);
                        if (a == b)
                        {
                            continue;
                        }

                        if (topLevel)
                        {
                            MismatchedTopLevel++;
                        }

                        var sub = new KeyRange(range.Lo + (idx * w), Math.Min(range.Hi, range.Lo + ((idx + 1) * w) - 1));
                        if (Math.Max(a.Count, b.Count) <= o.LeafRows || sub.Width <= 1 || o.BisectionFactor < 2)
                        {
                            leaves.Add(sub);
                        }
                        else
                        {
                            next.Add((sub, Math.Max(1, CeilDiv(sub.Width, o.BisectionFactor))));
                        }
                    }
                }

                if (topLevel)
                {
                    FingerprintTime = sw.Elapsed;
                    sw.Restart();
                    topLevel = false;
                }

                pending = next;
            }

            LeafChunks = leaves.Count;
            var sRows = new List<RowHash>();
            var tRows = new List<RowHash>();
            foreach (var batch in leaves.Chunk(o.MaxBatch))
            {
                var (a, b) = await Both(s, t, x => x.GetRangeRowHashesAsync(table, batch, ct));
                sRows.AddRange(a);
                tRows.AddRange(b);
            }

            Diff(sRows, tRows, numericKeys: true);
            DrillDownTime = sw.Elapsed;
        }

        public async Task BucketsAsync(CancellationToken ct)
        {
            if (MaxCount == 0)
            {
                return;
            }

            var buckets = Math.Max(1, CeilDiv(MaxCount, o.ChunkRows));
            var sw = Stopwatch.StartNew();
            var (sc, tc) = await Both(s, t, x => x.GetBucketChunksAsync(table, buckets, ct));
            var all = sc.Keys.Union(tc.Keys).Order().ToList();
            TopLevelChunks = all.Count;
            var bad = all.Where(b => sc.GetValueOrDefault(b) != tc.GetValueOrDefault(b)).ToList();
            MismatchedTopLevel = bad.Count;
            LeafChunks = bad.Count;
            FingerprintTime = sw.Elapsed;
            sw.Restart();

            var sRows = new List<RowHash>();
            var tRows = new List<RowHash>();
            foreach (var batch in bad.Chunk(o.MaxBatch))
            {
                var (a, b) = await Both(s, t, x => x.GetBucketRowHashesAsync(table, buckets, batch, ct));
                sRows.AddRange(a);
                tRows.AddRange(b);
            }

            Diff(sRows, tRows, numericKeys: false);
            DrillDownTime = sw.Elapsed;
        }

        public async Task ClientSideAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            if (table.Chunking == ChunkingMode.Range)
            {
                await MergeJoinAsync(ct);
            }
            else
            {
                var (a, b) = await Both(s, t, async x =>
                {
                    var list = new List<RowHash>();
                    await foreach (var r in x.StreamClientRowHashesAsync(table, ct))
                    {
                        list.Add(r);
                    }

                    return list;
                });
                Diff(a, b, numericKeys: false);
            }

            FingerprintTime = sw.Elapsed;
        }

        /// <summary>Both streams are pumped concurrently through bounded channels so neither database waits for the other.</summary>
        private async Task MergeJoinAsync(CancellationToken ct)
        {
            var sc = Pump(s, ct);
            var tc = Pump(t, ct);
            var sr = sc.Reader;
            var tr = tc.Reader;
            RowHash? a = await NextAsync(sr, ct);
            RowHash? b = await NextAsync(tr, ct);
            while (a is not null || b is not null)
            {
                var cmp = a is null ? 1 : b is null ? -1 : long.Parse(a.Value.Key, CultureInfo.InvariantCulture).CompareTo(long.Parse(b.Value.Key, CultureInfo.InvariantCulture));
                if (cmp == 0)
                {
                    if (!string.Equals(a!.Value.Digest, b!.Value.Digest, StringComparison.Ordinal))
                    {
                        AddDiff(a.Value.Key, DiffKind.Changed, a.Value.Digest, b.Value.Digest);
                    }

                    a = await NextAsync(sr, ct);
                    b = await NextAsync(tr, ct);
                }
                else if (cmp < 0)
                {
                    AddDiff(a!.Value.Key, DiffKind.MissingInTarget, a.Value.Digest, null);
                    a = await NextAsync(sr, ct);
                }
                else
                {
                    AddDiff(b!.Value.Key, DiffKind.ExtraInTarget, null, b.Value.Digest);
                    b = await NextAsync(tr, ct);
                }
            }
        }

        private Channel<RowHash> Pump(IRowSourceSession session, CancellationToken ct)
        {
            var channel = Channel.CreateBounded<RowHash>(new BoundedChannelOptions(50_000) { SingleReader = true, SingleWriter = true });
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (var r in session.StreamClientRowHashesAsync(table, ct))
                        {
                            await channel.Writer.WriteAsync(r, ct);
                        }

                        channel.Writer.Complete();
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.Complete(ex);
                    }
                },
                ct);
            return channel;
        }

        private static async Task<RowHash?> NextAsync(ChannelReader<RowHash> reader, CancellationToken ct) =>
            await reader.WaitToReadAsync(ct) && reader.TryRead(out var r) ? r : null;

        private void Diff(List<RowHash> sRows, List<RowHash> tRows, bool numericKeys)
        {
            var sMap = sRows.ToDictionary(r => r.Key, r => r.Digest, StringComparer.Ordinal);
            var tMap = tRows.ToDictionary(r => r.Key, r => r.Digest, StringComparer.Ordinal);
            IEnumerable<string> keys = sMap.Keys.Union(tMap.Keys);
            keys = numericKeys
                ? keys.OrderBy(k => long.Parse(k, CultureInfo.InvariantCulture))
                : keys.Order(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                var hasS = sMap.TryGetValue(k, out var sd);
                var hasT = tMap.TryGetValue(k, out var td);
                if (hasS && hasT && string.Equals(sd, td, StringComparison.Ordinal))
                {
                    continue;
                }

                AddDiff(k, !hasT ? DiffKind.MissingInTarget : !hasS ? DiffKind.ExtraInTarget : DiffKind.Changed, sd, td);
            }
        }

        private void AddDiff(string key, DiffKind kind, string? sd, string? td)
        {
            if (_diffs.Count >= o.MaxDiffs)
            {
                Truncated = true;
                return;
            }

            var display = table.Chunking == ChunkingMode.Range ? key : Canonical.Canonicalizer.DisplayKey(key);
            _diffs.Add(new RowDiff(key, display, kind, sd, td, []));
        }

        /// <summary>Names the differing columns of changed rows; only these rows are read field by field.</summary>
        public async Task DetailsAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var changed = _diffs.Where(d => d.Kind == DiffKind.Changed).Select(d => d.Key).ToList();
            var sFields = new Dictionary<string, RowFields>(StringComparer.Ordinal);
            var tFields = new Dictionary<string, RowFields>(StringComparer.Ordinal);
            var clientSide = o.Strategy == CompareStrategy.ClientSide;
            foreach (var batch in changed.Chunk(o.MaxBatch))
            {
                var (a, b) = await Both(s, t, x => x.GetRowFieldsAsync(table, batch, clientSide, ct));
                foreach (var r in a)
                {
                    sFields[r.Key] = r;
                }

                foreach (var r in b)
                {
                    tFields[r.Key] = r;
                }
            }

            for (var i = 0; i < _diffs.Count; i++)
            {
                var d = _diffs[i];
                if (d.Kind != DiffKind.Changed || !sFields.TryGetValue(d.Key, out var sf) || !tFields.TryGetValue(d.Key, out var tf))
                {
                    continue;
                }

                var cols = new List<ColumnDiff>();
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    if (string.Equals(sf.Fields[c], tf.Fields[c], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var column = table.Columns[c];
                    var (kind, detail) = DifferenceClassifier.Classify(column, sf.Fields[c], tf.Fields[c]);
                    cols.Add(new ColumnDiff(
                        column.Name,
                        kind,
                        detail,
                        o.IncludeValues ? DifferenceClassifier.Decode(sf.Fields[c]) ?? "NULL" : null,
                        o.IncludeValues ? DifferenceClassifier.Decode(tf.Fields[c]) ?? "NULL" : null));
                }

                _diffs[i] = d with { Columns = cols };
            }

            DetailsTime = sw.Elapsed;
        }

        private static long Min(long? a, long? b) => (a, b) switch
        {
            (null, null) => 0,
            (null, { } y) => y,
            ({ } x, null) => x,
            ({ } x, { } y) => Math.Min(x, y),
        };

        private static long Max(long? a, long? b) => (a, b) switch
        {
            (null, null) => 0,
            (null, { } y) => y,
            ({ } x, null) => x,
            ({ } x, { } y) => Math.Max(x, y),
        };

        private static long CeilDiv(long a, long b) => (a + b - 1) / b;
    }
}
