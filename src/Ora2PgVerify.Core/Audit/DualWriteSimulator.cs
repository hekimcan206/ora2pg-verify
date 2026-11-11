using System.Diagnostics;
using System.Globalization;
using Ora2PgVerify.Synthetic;

namespace Ora2PgVerify.Audit;

/// <summary>Executes one parameterised statement on one engine (the write path of the simulated application).</summary>
public interface IAccountWriter : IAsyncDisposable
{
    Task UpdateAccountAsync(long accountId, decimal balance, DateTime updatedAt, CancellationToken ct);
}

public enum DualWriteFault
{
    /// <summary>The PostgreSQL write never happens (a swallowed exception in the second write).</summary>
    LostWrite,

    /// <summary>The PostgreSQL write carries local time instead of UTC (a time zone bug in the new write path).</summary>
    WrongValue,

    /// <summary>An older update reaches PostgreSQL after a newer one (out-of-order delivery through a queue).</summary>
    StaleOverwrite,
}

public sealed record InjectedFault(long AccountId, DualWriteFault Fault, DateTimeOffset At)
{
    public string Key => AccountId.ToString(CultureInfo.InvariantCulture);
}

public sealed record SimulatorOptions
{
    public int Accounts { get; init; } = 20_000;

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(20);

    public int WritesPerSecond { get; init; } = 100;

    /// <summary>The PostgreSQL write lands up to this long after the Oracle write (replication / queue lag).</summary>
    public TimeSpan MaxLag { get; init; } = TimeSpan.FromMilliseconds(500);

    public int FaultsPerKind { get; init; } = 3;

    public int Seed { get; init; } = 7;
}

public sealed record SimulatorResult(long Writes, IReadOnlyList<InjectedFault> Faults, TimeSpan Elapsed);

/// <summary>
/// Simulates an application during cut-over: every update goes to Oracle immediately and to PostgreSQL after a random
/// lag (so honest writes are briefly divergent), with a few deliberate faults. An account that received a fault is
/// not written again, so the fault is the ground truth the auditor must confirm, while the lagging writes on all
/// other accounts are the noise it must not report.
/// </summary>
public sealed class DualWriteSimulator(IAccountWriter oracle, IAccountWriter postgres, SimulatorOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<SimulatorResult> RunAsync(CancellationToken ct)
    {
        var rnd = new Random(options.Seed);
        var balances = new Dictionary<long, decimal>();
        var poisoned = new HashSet<long>();
        var lastDue = new Dictionary<long, DateTimeOffset>();
        var faults = new List<InjectedFault>();
        var pending = new PriorityQueue<(long Id, decimal Balance, DateTime UpdatedAt), DateTimeOffset>();
        var sw = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / options.WritesPerSecond);
        var totalWrites = (long)(options.Duration.TotalSeconds * options.WritesPerSecond);
        var plan = FaultSchedule(totalWrites, rnd);
        long writes = 0;

        while (writes < totalWrites && !ct.IsCancellationRequested)
        {
            var id = (long)rnd.Next(1, options.Accounts + 1);
            if (poisoned.Contains(id))
            {
                continue;
            }

            var now = _clock.GetUtcNow();
            var stamp = Truncate(now.UtcDateTime);
            var balance = decimal.Round((decimal)(rnd.NextDouble() * 10_000), 2);
            // The value PostgreSQL would get from an older, delayed message: the last one we wrote, or any other value.
            var previous = balances.TryGetValue(id, out var last) && last != balance ? last : balance + 1m;
            balances[id] = balance;
            await oracle.UpdateAccountAsync(id, balance, stamp, ct);

            // Honest writes keep per-account order (a partitioned queue): only the injected StaleOverwrite reorders them.
            var due = now + TimeSpan.FromMilliseconds(rnd.NextDouble() * options.MaxLag.TotalMilliseconds);
            if (lastDue.TryGetValue(id, out var previousDue) && due <= previousDue)
            {
                due = previousDue + TimeSpan.FromTicks(1);
            }

            lastDue[id] = due;
            if (plan.TryGetValue(writes, out var fault))
            {
                poisoned.Add(id);
                faults.Add(new InjectedFault(id, fault, now));
                switch (fault)
                {
                    case DualWriteFault.LostWrite:
                        break;
                    case DualWriteFault.WrongValue:
                        pending.Enqueue((id, balance, stamp.AddHours(3)), due);
                        break;
                    case DualWriteFault.StaleOverwrite:
                        pending.Enqueue((id, balance, stamp), due);
                        pending.Enqueue((id, previous, stamp.AddMilliseconds(-1)), due + options.MaxLag);
                        break;
                }
            }
            else
            {
                pending.Enqueue((id, balance, stamp), due);
            }

            writes++;
            await DrainAsync(pending, _clock.GetUtcNow(), ct);
            var next = interval * writes - sw.Elapsed;
            if (next > TimeSpan.Zero)
            {
                await Task.Delay(next, _clock, ct);
            }
        }

        while (pending.Count > 0 && !ct.IsCancellationRequested)
        {
            await DrainAsync(pending, DateTimeOffset.MaxValue, ct);
        }

        return new SimulatorResult(writes, faults, sw.Elapsed);
    }

    private async Task DrainAsync(PriorityQueue<(long Id, decimal Balance, DateTime UpdatedAt), DateTimeOffset> pending, DateTimeOffset upTo, CancellationToken ct)
    {
        while (pending.TryPeek(out var w, out var due) && due <= upTo)
        {
            pending.Dequeue();
            await postgres.UpdateAccountAsync(w.Id, w.Balance, w.UpdatedAt, ct);
        }
    }

    /// <summary>Spreads the faults evenly over the first 80% of the run so the last ones still have time to be confirmed.</summary>
    private Dictionary<long, DualWriteFault> FaultSchedule(long totalWrites, Random rnd)
    {
        var kinds = Enum.GetValues<DualWriteFault>();
        var count = kinds.Length * options.FaultsPerKind;
        var plan = new Dictionary<long, DualWriteFault>();
        for (var i = 0; i < count; i++)
        {
            var at = (long)((i + 0.5) * totalWrites * 0.8 / count) + rnd.Next(0, 3);
            plan[at] = kinds[i % kinds.Length];
        }

        return plan;
    }

    private static DateTime Truncate(DateTime dt) => new(dt.Ticks - (dt.Ticks % 10), DateTimeKind.Unspecified);
}

public sealed record AuditScore(int Injected, int Confirmed, int TruePositives, int FalsePositives, int FalseNegatives, TimeSpan? MedianTimeToConfirm, TimeSpan? MaxTimeToConfirm, int Transients, TimeSpan? MedianTransient, TimeSpan? MaxTransient)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => Injected == 0 ? 1 : (double)TruePositives / Injected;

    public static AuditScore Of(SimulatorResult sim, AuditResult audit)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(audit);
        var injected = sim.Faults.ToDictionary(f => f.Key);
        var tp = audit.Confirmed.Where(c => injected.ContainsKey(c.Key)).ToList();
        var latencies = tp.Select(c => c.ConfirmedAt - injected[c.Key].At).Order().ToList();
        var transients = audit.Transients.Select(t => t.Duration).Order().ToList();
        return new AuditScore(
            sim.Faults.Count,
            audit.Confirmed.Count,
            tp.Count,
            audit.Confirmed.Count - tp.Count,
            sim.Faults.Count - tp.Count,
            Median(latencies),
            latencies.Count == 0 ? null : latencies[^1],
            transients.Count,
            Median(transients),
            transients.Count == 0 ? null : transients[^1]);
    }

    private static TimeSpan? Median(List<TimeSpan> sorted) => sorted.Count == 0 ? null : sorted[sorted.Count / 2];
}

/// <summary>Accounts table names used by the dual-write demo.</summary>
public static class AccountsSchema
{
    public static string OracleTable => $"{SyntheticSchema.OracleSchema}.ACCOUNTS";

    public static string PostgresTable => $"{SyntheticSchema.PostgresSchema}.accounts";
}
