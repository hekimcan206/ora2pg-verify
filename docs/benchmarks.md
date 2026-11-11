# Benchmarks

All numbers on this page were measured on the machine described below with `ora2pg-verify bench`. Nothing is
estimated or rounded up. Raw output: [`benchmarks/results/bench.md`](../benchmarks/results/bench.md) and
[`bench.json`](../benchmarks/results/bench.json).

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores / 16 logical CPUs), laptop |
| RAM | 15.7 GB host; Docker Desktop VM limited to 7.6 GB |
| OS | Windows 11 Home 10.0.26200, Docker Desktop 29.9 (WSL2 backend) |
| .NET | 10.0 (SDK 10.0.401), Release build, CLI running on the host |
| Oracle | `gvenzl/oracle-free:23-slim-faststart` (Oracle Database 23ai Free: the Free edition caps the instance at 2 CPU threads and 2 GB of SGA+PGA) |
| PostgreSQL | `postgres:17-alpine`, default configuration |
| Host load | Only this project's three containers were running (Oracle ~2.7 GB RSS, PostgreSQL ~0.3 GB, nginx). Another project's containers were stopped at the time. The laptop was otherwise idle but on AC power with default Windows power settings, so turbo behaviour is not controlled. |

## Dataset

`ora2pg-verify seed --rows 1000000 --seed 42 --per-kind 10` (the same thing `DEMO_ROWS=1000000 docker compose up`
runs):

| Table | Rows | Key | Chunking | Notes |
|---|---:|---|---|---|
| orders | 1,000,000 (999,995 after defects) | NUMBER(12) | range | CHAR(10)→varchar(10), DATE→timestamp(0), TIMESTAMP WITH TIME ZONE with four different offsets→timestamptz, Turkish and 4-byte UTF-8 notes, 40% NULL notes |
| customers | 50,000 | VARCHAR2(12) | hash bucket | Turkish names (Bogus `tr`), DATE→date, CHAR(1) |
| order_documents | 20,000 | (NUMBER, NUMBER) | hash bucket | CLOB 1–6 KB each (~70 MB of text) |

Loading took 102.7 s into Oracle (array binding, 5,000 rows per round trip) and 8.5 s into PostgreSQL (binary
COPY), in parallel.

The seeder then injects 100 defects (10 kinds × 10 rows, chosen from the seed) into PostgreSQL (and two kinds
into Oracle), and records them in a manifest.

## Method

- One warm-up comparison per configuration (connection pools, JIT, database buffer caches), then N measured
  runs. The table reports the **median** and the min/max. N = 5 for the strategy and hash rows, 3 for the
  chunk-size rows.
- Each run opens fresh read-only snapshot sessions on both engines. The two engines work in parallel, so the
  wall time is the slower engine plus the client.
- "Rows transferred" counts result rows received from both databases: fingerprint rows, row digests, field rows
  and, for the client-side strategy, every raw row.

## Results

### Strategy: pushdown versus streaming (5 runs, quiet host)

| Table | Strategy | Median | Min–max | Oracle busy | PostgreSQL busy | Rows transferred |
|---|---|---:|---:|---:|---:|---:|
| orders (1 M) | database-side, 10 k chunks | 8,992 ms | 8,594–9,129 | 8,678 ms | 6,043 ms | 83,068 |
| orders (1 M) | client-side | 13,774 ms | 13,154–16,114 | 13,758 ms | 13,279 ms | 2,000,092 |
| customers (50 k) | database-side, 10 k buckets | 645 ms | 560–964 | 524 ms | 554 ms | 100,022 |
| customers (50 k) | client-side | 577 ms | 503–693 | 511 ms | 373 ms | 100,012 |
| order_documents (20 k CLOBs) | database-side, 10 k buckets | 1,631 ms | 1,436–1,936 | 1,557 ms | 1,223 ms | 40,026 |
| order_documents (20 k CLOBs) | client-side | 2,984 ms | 2,711–3,141 | 2,961 ms | 2,296 ms | 40,022 |

"Busy" is time spent waiting on that engine (execute plus fetch). The two engines work in parallel, so wall time ≈ the
busier engine. With 10 k-row buckets, `customers` has 5 buckets and 20 scattered defects, so every bucket is bad
and the drill-down reads everything. That is the worst case for checksumming, and the chunk sweep below fixes it.

### Chunk size (3 runs, repeated on a quiet host: foreign containers below 4 % CPU, sampled every 15 s)

| Table | Chunk rows | Median | Fingerprint | Drill-down | Oracle busy | PostgreSQL busy | Queries | Rows transferred |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| orders | 500 | 5,585 ms | 5,273 ms | 270 ms | 5,508 ms | 2,441 ms | 8 | 69,224 |
| orders | 1,000 | 6,502 ms | 5,775 ms | 676 ms | 6,471 ms | 3,129 ms | 136 | 12,276 |
| orders | 2,000 | 6,612 ms | 5,726 ms | 806 ms | 6,553 ms | 3,876 ms | 134 | 19,520 |
| orders | 5,000 | 6,834 ms | 5,111 ms | 1,682 ms | 6,786 ms | 4,657 ms | 126 | 43,572 |
| orders | 10,000 | 8,150 ms | 5,233 ms | 2,877 ms | 8,105 ms | 5,859 ms | 104 | 83,068 |
| orders | 100,000 | 12,371 ms | 5,166 ms | 7,168 ms | 12,324 ms | 8,244 ms | 146 | 53,868 |
| orders | 1,000,000 | 16,537 ms | 5,067 ms | 11,428 ms | 16,502 ms | 10,395 ms | 162 | 34,766 |
| customers | 100 | 278 ms | 215 ms | 52 ms | 230 ms | 275 ms | 8 | 4,646 |
| customers | 1,000 | 320 ms | 195 ms | 111 ms | 292 ms | 306 ms | 8 | 34,366 |
| customers | 10,000 | 456 ms | 215 ms | 229 ms | 389 ms | 438 ms | 8 | 100,022 |

Reading it:

- The fingerprint pass costs about the same (5.1–5.8 s) at every chunk size. It is one grouped full scan per
  side, dominated by hashing on Oracle Free's two CPU threads; the number of groups barely matters.
- Drill-down is what chunk size buys. Large chunks have to be bisected level by level, and each level
  re-fingerprints the bad ranges: 11.4 s at 1 M-row chunks, 0.27 s at 500.
- At 500 rows every bad chunk is already a leaf, so the whole drill-down is one batched digest query per side
  (8 queries in total), at the price of fetching 500 rows per bad chunk (69 k rows). At 1,000 rows, a
  chunk is occasionally a little over the leaf limit and gets one bisection level. That transfers the fewest
  rows (12 k) for 0.9 s more. **1,000 became the default.** At 500 the run is faster on localhost, but over a
  slow link the transfer matters.
- Text keys in hash buckets behave the same way: 100-row buckets read 4.6 k rows instead of 100 k.

### Hash algorithm (5 runs)

| Configuration | Median | Min–max |
|---|---:|---:|
| MD5, 10 k chunks | 8,992 ms | 8,594–9,129 |
| SHA-256, 10 k chunks | 9,885 ms | 9,705–10,594 |

SHA-256 costs about 10 % more. This row may overlap the foreign load noted in `bench.md`, so treat the difference
as an upper bound.

### Detection on the injected set

Every one of the 100 injected defects was reported at the exact row with the right kind. Every changed row named
exactly the injected column and nothing else, every diagnosis matched, and no row outside the manifest was reported
among 1,069,995 compared rows (precision 100 %):

| Defect | Table | Injected | Detected | Exact column | Classified |
|---|---|---:|---:|---:|---:|
| ORD_MISSING (deleted in PostgreSQL) | orders | 10 | 10 | 10 | 10 |
| ORD_EXTRA (5 beyond Oracle's MAX(key), 5 deleted in Oracle) | orders | 10 | 10 | 10 | 10 |
| ORD_AMOUNT_DRIFT (+0.01) | orders | 10 | 10 | 10 | 10 |
| ORD_EMPTY_NOTE (NULL → '') | orders | 10 | 10 | 10 | 10 |
| ORD_TRAILING_SPACE | orders | 10 | 10 | 10 | 10 |
| ORD_TIME_TRUNCATED (DATE → midnight) | orders | 10 | 10 | 10 | 10 |
| ORD_TZ_SHIFT (+3 h) | orders | 10 | 10 | 10 | 10 |
| CUS_CASE (upper-cased Turkish names) | customers | 10 | 10 | 10 | 10 |
| CUS_MISSING | customers | 10 | 10 | 10 | 10 |
| DOC_CLOB_EDIT (one character in a CLOB) | order_documents | 10 | 10 | 10 | 10 |

Representation differences present on **every** row (CHAR(10) stored unpadded, TIMESTAMP WITH TIME ZONE written
with four different offsets, `numeric(14,2)` printing `12.50`, Oracle DATE → PostgreSQL `date`) produced no findings.

### Dual-write audit

`accounts` (20,000 rows), 100 writes/s for 20 s, PostgreSQL writes delayed by up to 500 ms (FIFO per account),
3 faults of each kind (lost write, local time instead of UTC, out-of-order overwrite), rounds every second:

| Measure | Value |
|---|---|
| Faults confirmed | 9 / 9 |
| False positives | 0 |
| Transient (lag) differences ignored | 434 (median duration 1.13 s, max 2.26 s) |
| Time from fault to confirmation | median 3.04 s, max 3.80 s |
| Audit round (full chunked comparison of 20 k rows) | median 127 ms over 23 rounds |

### PL/SQL scan

60 findings (8 high, 18 medium, 26 low, 8 info) on the sample schema, median 47 ms after warm-up.

## How to reproduce

```bash
DEMO_ROWS=1000000 docker compose up -d          # seeds 1 M rows and runs the demo once
docker compose cp verify:/out/manifest.json ./manifest.json
export ORA2PGV_ORACLE="Data Source=localhost:25621/FREEPDB1;User Id=verify_reader;Password=ReaderDev1"
export ORA2PGV_POSTGRES="Host=localhost;Port=25632;Database=appdb;Username=verify_reader;Password=ReaderDev1"
dotnet run -c Release --project src/Ora2PgVerify.Cli -- bench --expect manifest.json --runs 5 --host-note "..."
```
