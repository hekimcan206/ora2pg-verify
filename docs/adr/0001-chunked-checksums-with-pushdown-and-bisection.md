# 1. Chunked checksums computed in the database, with bisection down to the row

- Status: accepted
- Date: 2026-11-11

## Context

After an Oracle → PostgreSQL migration someone has to sign off that the data arrived intact. "Row counts
match" is the usual evidence and it is weak. It misses every changed value, and a deleted row plus an
inserted row cancel out. The comparison has to:

- find **every** difference, including one changed column in one of a million rows;
- say **exactly which rows** differ (and ideally which columns and why), not just "table differs";
- run against production-sized tables without copying them, and with read-only accounts;
- work across two engines that share no hash function, number formatting, time zone rules or collation.

## Options considered

1. **Count rows per table.** Cheap, and it misses almost every interesting defect.
2. **Stream every row to a client and compare** (AWS DMS validation compares source and target rows in
   primary-key partitions of 10,000 rows by default, with several validation threads, see [DMS data validation][dms]).
   This is simple and exact. It moves the whole table over the network twice and needs the client to apply the
   type normalisation. It is the baseline here (`--strategy client`).
3. **Checksums per chunk computed inside each database, then drill down** (data-diff's divide-and-conquer with
   a bisection factor of 10 and local download below a threshold, see [data-diff technical explanation][datadiff];
   pg_comparator's folding checksum tree with 128-row folds, which works best when differences are few,
   see [pg_comparator][pgc]). Only fingerprints cross the network until a chunk disagrees.
4. **A vendor aggregate** (Oracle 21c+ `CHECKSUM`, PostgreSQL extensions). Each engine has its own, so the
   two values cannot be compared.

## Decision

Option 3, with option 2 kept as a cross-check and baseline.

- **Fingerprint = (COUNT(\*), SUM(hash60))** per chunk, where `hash60` is the first 60 bits of the row digest
  (MD5 by default, SHA-256 optional) taken as an integer. A SUM is order-independent and, unlike XOR,
  sensitive to duplicated rows. 2⁶⁰ × 1 M rows ≈ 1.2 × 10²⁴, which is exact in Oracle `NUMBER` (38 digits)
  and PostgreSQL `numeric`, and fits System.Decimal on the client. 60 bits rather than 64 keep every
  intermediate value non-negative in PostgreSQL `bigint`.
- **One grouped scan per side** returns every chunk's fingerprint (`GROUP BY floor((key-lo)/width)`), instead
  of one query per chunk. A million-row table costs two scans, not two hundred round trips.
- **Integer keys use range chunks** that are split `BisectionFactor` (16) ways when they disagree, until a
  chunk holds at most 1,000 rows. That chunk then returns *key + digest* per row, which pinpoints the rows.
  Range predicates use the primary key index.
- **Text and composite keys use hash buckets** (`MD5(canonical key) mod n`). Range boundaries on a text key
  depend on collation: Oracle `BINARY`/`NLS_SORT` versus PostgreSQL's ICU or libc collation give different
  orders, so the "same" range would hold different rows on each side. Buckets have no order, so they do
  not depend on collation. The cost is a full scan per drill-down batch (all bad buckets go in one query).
- **Only differing rows are read field by field** to name the columns and classify the difference (empty
  string vs NULL, time of day lost, time zone shift, numeric drift, trailing whitespace, case).
- **Each side reads one snapshot**: Oracle `SET TRANSACTION READ ONLY`, PostgreSQL
  `REPEATABLE READ READ ONLY`. Fingerprints and drill-down therefore see the same data.

## Consequences

- With few differences, network traffic is proportional to the number of chunks plus about 1,000 rows per bad
  leaf, not to table size. Measured on 1 M rows, the pushdown moves orders of magnitude fewer rows than streaming
  (docs/benchmarks.md).
- Chunk size is a real trade-off and is measured: small chunks cost more GROUP BY groups and fingerprint rows,
  large chunks cost more drill-down when there are many scattered defects. On 1 M rows with 70 scattered
  defects, the fingerprint pass cost the same at every size (about 5.5 s), while drill-down went from 0.7 s
  at 1,000-row chunks to 11.4 s at 1 M-row chunks. The default is therefore 1,000 rows, the same as the leaf
  size (docs/benchmarks.md).
- When most rows differ, checksumming saves nothing. data-diff and Datafold say the same; at that point the
  client-side strategy is as good.
- MD5/SHA-256 detect accidental differences. Someone who can write to both databases can forge a matching
  fingerprint. This is a verification tool, not a tamper-evidence mechanism (docs/threat-model.md).
- A truncated 60-bit hash summed over a chunk could in principle collide (two different chunks with equal
  COUNT and SUM). For accidental differences, the chance of a missed change is about 2⁻⁶⁰ per chunk. The
  client-side strategy and the row-level drill-down use the full digest.

[dms]: https://docs.aws.amazon.com/dms/latest/userguide/CHAP_Validating.html
[datadiff]: https://data-diff.readthedocs.io/en/latest/technical-explanation.html
[pgc]: https://manpages.ubuntu.com/manpages/jammy/man1/pg_comparator.1.html
