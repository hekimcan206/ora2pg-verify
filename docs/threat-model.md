# Threat model

ora2pg-verify connects to two production databases during a migration and writes a report that people will
attach to tickets and send around. That combination, broad read access plus a portable artefact, is what
this document is about.

## Assets

| Asset | Why it matters |
|---|---|
| Database credentials (Oracle and PostgreSQL) | A migration often uses powerful accounts. Leaking one exposes the whole database. |
| Table contents | Production data; often personal data (KVKK, the Turkish GDPR, and GDPR apply). |
| Stored code (PL/SQL source) | Can contain hard-coded passwords, endpoints and business rules. |
| The report (HTML/JSON) | Leaves the database perimeter by design: e-mail, tickets, chat. |
| Correctness of the verdict | A false "data matches" lets a bad cut-over through. |

## Actors

- **Operator** running the CLI, trusted but fallible: may paste a connection string into a shared terminal.
- **Report reader**: auditor, manager or vendor. Authorised to see *that* data differs, not necessarily the data.
- **Other users of the machine or CI runner**: can read the process list and environment of other processes in
  some setups.
- **Someone with write access to one or both databases**: could, in principle, try to make a difference
  invisible.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation | Residual risk |
|---|---|---|---|
| S1 | **Credential exposure through the command line.** argv is visible in `ps` / Task Manager to other users. | Connection strings are read from environment variables (`ORA2PGV_ORACLE`, `ORA2PGV_POSTGRES`, `..._OWNER`) by default; options exist but the help text names the variables first. | Environment variables are visible to the same user and to root. Use a secrets manager or CI secret injection. |
| S2 | **Credentials in the report or logs.** | Reports carry `host:port/service` only, built from the parsed connection string, never the string itself. Nothing logs connection strings. An integration test writes both report formats and asserts that neither password nor the word "Password" appears; the compose CI job checks the served report. | — |
| T1 | **The tool modifies data.** A bug, or a wrong connection string pointing at the owner account. | Comparison and scan accounts are read-only at three levels: (1) grants: `VERIFY_READER` has `CREATE SESSION` + `SELECT` via a role, `verify_reader` has `CONNECT`/`USAGE`/`SELECT`; (2) PostgreSQL `default_transaction_read_only = on` for the role; (3) every comparison runs in a read-only transaction (Oracle `SET TRANSACTION READ ONLY`, PostgreSQL `REPEATABLE READ READ ONLY`). Integration tests prove the reader accounts cannot `UPDATE` on either engine. Owner accounts are only used by `seed` and the dual-write simulator, and only on the synthetic schema. | `SELECT_CATALOG_ROLE` (Oracle, for `DBA_SOURCE`) is broad *metadata* read access. Without it the scan falls back to `ALL_SOURCE` and says so. |
| T2 | **Hidden divergence.** Someone with write access to both sides alters a value on both, or crafts a value whose truncated hash collides. | Out of scope by design: the checksums detect accidental divergence (migration bugs, lost writes). MD5 is selectable to SHA-256, but neither protects against an adversary who controls both databases; the report's Method section says so. | Use database auditing or a tamper-evident log for adversarial integrity. |
| T3 | **SQL injection through identifiers.** Table and column names are spliced into generated SQL. | Identifiers come from the catalogs, must match `^[A-Za-z_][A-Za-z0-9_$#]{0,127}$`, and are double-quoted; anything else is refused, not escaped. All values (ranges, buckets, keys, owners) are bound parameters. Unit tests cover refusal. | Tables with unusual (quoted, mixed-case with spaces) names cannot be compared. That is deliberate. |
| I1 | **Data leaking through the report.** Row values of differing rows are the most useful debugging aid and the most sensitive content. | Values are **off by default**: the report shows keys, column names, the kind of difference ("empty string vs NULL", "+0.01", "shifted by +3:00:00") and the counts. `--include-values` opts in for one run. CLOB values are never shown, only digests. When included, values are HTML-encoded (tested against `<script>`). | Keys themselves can be personal data (a national ID used as a primary key). Restrict who receives the report, or extend the tool with keyed pseudonyms for keys (next steps). |
| I2 | **Secrets in stored code** surfacing in scan snippets. | String literal contents are replaced with `'…'` in snippets by default (`--show-literals` to opt in). | Identifiers and comments are not redacted. |
| I3 | **The report links to external resources** and leaks that it was opened (tracking pixels, CDNs). | The HTML is self-contained: inline CSS, no scripts, no external requests. A unit test asserts it. | — |
| D1 | **Load on production.** A full scan of a large table during business hours. | Pushdown keeps transfer small, but each fingerprint pass is still a full scan per side. Run against a replica or a quiet window; chunk size and table list are configurable; PostgreSQL reader has a `statement_timeout`. Long Oracle read-only transactions need undo retention (ORA-01555). | No built-in rate limiting. |
| R1 | **No trace of who verified what.** | The JSON report records version, time, endpoints, schemas, strategy, chunk size, hash and empty-string policy, so a sign-off can be reproduced. | Reports are not signed. |
| E1 | **Container compromise** gives a shell. | Chiseled runtime image: no shell, no package manager, non-root UID 1654 (checked in CI). | — |

## Data handling note

All data in this repository is synthetic (generated by `SyntheticData` with a fixed seed). No real personal,
customer or production data is used in tests, demos or benchmarks.
