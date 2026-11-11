# 3. Dual-write audit: confirm a divergence only when both sides are stable

- Status: accepted
- Date: 2026-11-11

## Context

Zero-downtime cut-overs run both databases at once for a while. That can be dual writes from the
application, or replication plus writes on one side, with reads moving first and writes later. The risk
in this window is silent divergence: a second write swallowed by an exception handler, a time zone bug
in the new code path, an out-of-order message that overwrites a newer value. Practitioner write-ups agree
that shadow reads and continuous comparison catch these before users do, and that "silence is not success"
([zero-downtime migration failure modes][hn], [dual write patterns][patterns]).

The difficulty is that two engines cannot be read at one instant. Oracle's SCN and PostgreSQL's snapshot
are unrelated. Any comparison of live data sees writes that have reached one side and not yet the other.

## Options considered

1. **Compare once after writes stop.** This is correct, but too late: the whole point is to catch divergence
   while you can still roll back cheaply.
2. **Compare rows older than a watermark** (`updated_at < now() - lag`). It needs a trustworthy modification
   timestamp on every table, and it is blind to the time zone bug it should catch (the timestamp itself is
   wrong).
3. **CDC-based comparison** (read both change streams and compare events). It needs LogMiner/Debezium on Oracle
   and logical decoding on PostgreSQL, which is heavy infrastructure for a verification tool. That belongs to
   a CDC project.
4. **Repeated chunked comparisons with stability-based confirmation.** AWS DMS's CDC validation does the
   same in spirit: it re-checks mismatched rows before failing them and reports rows "not comparable because
   of in-flight changes" ([DMS data validation][dms]).

## Decision

Option 4. Every round runs the normal chunked comparison (ADR 0001), so it is cheap when the tables agree,
with each side in its own read-only snapshot. Every differing key is tracked with the pair
(Oracle digest, PostgreSQL digest):

- the **same pair** seen in `ConfirmRounds` (3) consecutive rounds means both sides are quiet and still
  disagree: **confirmed divergence**, reported with columns and diagnosis;
- a key that **disappears** from the differences converged: **transient** (lag), and its duration is
  recorded;
- a key whose pair **keeps changing** is **unstable**: it is being written, and is not judged until it
  settles.

A confirmed key that later converges (a re-sync) stays in the report.

## Consequences

- Detection latency is about `ConfirmRounds × round interval` plus one round. In the demo that is about
  3–4 s at 1 s rounds. The measured median is in the README.
- An account written continuously, more often than the round interval, is never judged. It is counted as
  unstable rather than passed. Lowering the interval helps, as does running the audit after the hot path quiets.
- The ground-truth simulator writes Oracle immediately and PostgreSQL after up to 500 ms of lag. It keeps
  per-account order, as a partitioned queue would, and injects three faults: lost write, wrong value
  (local time instead of UTC) and stale overwrite (out-of-order delivery). The first implementation used
  independent lags per write and so reordered honest writes. That was a real divergence, unlabelled, and the
  integration test caught it as "false positives". The fix models the queue correctly; the episode is a
  reminder that ordering is part of the contract of any dual-write path.
- Lag longer than `ConfirmRounds × interval` would be confirmed as divergence. That is arguably right: a
  replica that far behind is not ready for cut-over. Tune the interval to the replication SLO.

[dms]: https://docs.aws.amazon.com/dms/latest/userguide/CHAP_Validating.html
[hn]: https://hackernoon.com/why-most-database-migrations-fail-silently-5-hidden-failure-modes-in-zero-downtime-replatforming
[patterns]: https://archman.dev/docs/modernization-and-migration/data-migration-dual-writes-backfill-cdc
