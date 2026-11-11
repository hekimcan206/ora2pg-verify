using System.Diagnostics;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Audit;

public sealed record AuditOptions
{
    /// <summary>Pause between comparison rounds.</summary>
    public TimeSpan RoundInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A difference is confirmed when the same (Oracle digest, PostgreSQL digest) pair is seen in this many consecutive
    /// rounds: both sides are quiet and still disagree. Anything that resolves earlier was replication lag.
    /// </summary>
    public int ConfirmRounds { get; init; } = 3;

    public CompareOptions Compare { get; init; } = new() { ChunkRows = 1_000, LeafRows = 200 };
}

/// <summary>A row that stayed different while both sides were stable.</summary>
public sealed record ConfirmedDivergence(string Key, string DisplayKey, DiffKind Kind, IReadOnlyList<ColumnDiff> Columns, DateTimeOffset FirstSeen, DateTimeOffset ConfirmedAt, int Round);

/// <summary>A row that differed for a while and then converged (in-flight write, replication lag).</summary>
public sealed record TransientDifference(string Key, TimeSpan Duration, int Rounds);

public sealed record AuditRound(int Number, DateTimeOffset At, TimeSpan Elapsed, int Differences, int NewlyConfirmed, int Converged, int Unstable);

public sealed record AuditResult(
    string Table,
    IReadOnlyList<AuditRound> Rounds,
    IReadOnlyList<ConfirmedDivergence> Confirmed,
    IReadOnlyList<TransientDifference> Transients,
    int StillUnstable,
    AuditOptions Options);

/// <summary>
/// Detects divergence between two databases that are both being written during a cut-over (dual write, or
/// replication plus application writes).
/// <para>
/// The two engines cannot be read at one common instant, so a single comparison mixes real defects with writes
/// that reached one side first. Each round therefore runs a full chunked comparison (each side in its own read-only
/// snapshot) and tracks every differing key across rounds: a key whose digests on both sides stop changing but still
/// differ for <see cref="AuditOptions.ConfirmRounds"/> rounds is a confirmed divergence; a key that converges was lag;
/// a key whose digests keep moving is "unstable" and not judged yet (the same idea as AWS DMS's
/// "not comparable because of in-flight changes" and its CDC validation retries).
/// </para>
/// </summary>
public sealed class DualWriteAuditor(TableSpec table, IRowSource source, IRowSource target, AuditOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);
    private readonly List<ConfirmedDivergence> _confirmed = [];
    private readonly List<TransientDifference> _transients = [];
    private readonly List<AuditRound> _rounds = [];

    public IReadOnlyList<ConfirmedDivergence> Confirmed => _confirmed;

    /// <summary>Runs rounds until <paramref name="ct"/> is cancelled or <paramref name="maxRounds"/> is reached.</summary>
    public async Task<AuditResult> RunAsync(int maxRounds, CancellationToken ct)
    {
        for (var round = 1; round <= maxRounds && !ct.IsCancellationRequested; round++)
        {
            try
            {
                await RoundAsync(round, ct);
                await Task.Delay(options.RoundInterval, _clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        return Result();
    }

    public AuditResult Result() => new(
        table.Name,
        [.. _rounds],
        [.. _confirmed.OrderBy(c => c.Key, StringComparer.Ordinal)],
        [.. _transients],
        _tracked.Values.Count(t => !t.Confirmed),
        options);

    /// <summary>One comparison round; public so callers (and tests) can drive the timing themselves.</summary>
    public async Task<AuditRound> RoundAsync(int number, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var comparison = await new TableComparer(options.Compare).CompareAsync(table, source, target, ct);
        var now = _clock.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int confirmedNow = 0, unstable = 0;

        foreach (var d in comparison.Diffs)
        {
            seen.Add(d.Key);
            var pair = (d.SourceDigest, d.TargetDigest);
            if (!_tracked.TryGetValue(d.Key, out var t))
            {
                _tracked[d.Key] = new Tracked(pair, now, number) { Stable = 1 };
                continue;
            }

            if (t.Pair == pair)
            {
                t.Stable++;
            }
            else
            {
                t.Pair = pair;
                t.Stable = 1;
                unstable++;
            }

            if (!t.Confirmed && t.Stable >= options.ConfirmRounds)
            {
                t.Confirmed = true;
                confirmedNow++;
                _confirmed.Add(new ConfirmedDivergence(d.Key, d.DisplayKey, d.Kind, d.Columns, t.FirstSeen, now, number));
            }
        }

        var converged = 0;
        foreach (var key in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var t = _tracked[key];
            _tracked.Remove(key);
            if (t.Confirmed)
            {
                // Repaired after confirmation (e.g. a re-sync): it stays reported, with the repair visible in the rounds.
                continue;
            }

            converged++;
            _transients.Add(new TransientDifference(key, now - t.FirstSeen, number - t.FirstRound));
        }

        var r = new AuditRound(number, now, sw.Elapsed, comparison.Diffs.Count, confirmedNow, converged, unstable);
        _rounds.Add(r);
        return r;
    }

    private sealed class Tracked((string?, string?) pair, DateTimeOffset firstSeen, int firstRound)
    {
        public (string?, string?) Pair { get; set; } = pair;

        public DateTimeOffset FirstSeen { get; } = firstSeen;

        public int FirstRound { get; } = firstRound;

        public int Stable { get; set; }

        public bool Confirmed { get; set; }
    }
}
