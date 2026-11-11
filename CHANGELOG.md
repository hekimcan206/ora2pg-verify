# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-11

### Added

- **Row-level comparison with chunked checksums** (`compare`):
  - one canonical encoding implemented in Oracle SQL, PostgreSQL SQL and C#, proven equal field by field;
  - chunk fingerprints (COUNT, SUM of 60-bit row hashes) computed inside each database in one grouped scan;
  - range chunks with 16-way bisection for integer keys, and collation-independent hash buckets for text
    and composite keys;
  - drill-down to the exact rows, then to the differing columns, with a diagnosis: empty string vs NULL,
    NULL vs value, trailing whitespace, case only (Turkish dotted/dotless i aware), time of day lost, time
    shift, numeric drift, digest differs;
  - client-side strategy (stream and hash in .NET) as baseline and cross-check;
  - MD5 or SHA-256, empty strings reported or treated as NULL, one read-only snapshot per side;
  - column type mapping checks: DATE to date, NUMBER to float or integer, reduced scale, CHAR padding,
    time zones, unsupported types excluded with a finding.
- **PL/SQL and schema incompatibility scan** (`scan`): a lexer that ignores comments and literals; 26
  token rules plus 17 rated supplied packages; package state per declaration; sequences beyond bigint,
  synonyms, database links, global temporary tables, materialized views and risky column types from the
  dictionary; effort points per object; literals redacted in snippets.
- **Dual-write audit** (`audit-demo`): repeated chunked comparisons with stability-based confirmation
  (three consecutive rounds), transient lag tracking, and a simulator with lost writes, wrong values and
  out-of-order overwrites as ground truth.
- **Synthetic dataset and defect injection** (`seed`): deterministic orders, customers, CLOB documents and
  accounts on both engines; ten defect kinds recorded in a manifest; scoring of recall, precision,
  exact column and classification.
- **Report**: self-contained HTML (light and dark, no scripts) and JSON; values of differing columns only
  with `--include-values`; endpoints without credentials.
- **`demo` and `bench` commands**; `docker compose up` produces the report at
  `http://localhost:18090/report.html`.
- Tests: unit, FsCheck properties (encoding invariants; exact detection for any mutation set and chunking),
  golden files (pushdown SQL, scan, HTML report), Testcontainers integration on Oracle 23ai Free and
  PostgreSQL 17.
- CI: vulnerability audit, format check, warnings as errors, both-engine tests with coverage, container
  image check, compose demo; CodeQL.
- Docs: four ADRs, threat model, benchmarks with method and environment.
