# Contributing

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker. The integration tests start `gvenzl/oracle-free:23-slim-faststart` and `postgres:17-alpine` through
  Testcontainers, and `docker compose up` runs the demo.

## Workflow

1. Open an issue first for anything larger than a small fix.
2. Branch from `main`; one concern per pull request.
3. Before pushing:

   ```bash
   dotnet build                          # warnings are errors
   dotnet test                           # unit + FsCheck properties + golden + Oracle/PostgreSQL integration
   dotnet format --verify-no-changes
   ```

## Changing the canonical encoding

The encoding has three implementations that must stay equal (ADR 0002):

1. `src/Ora2PgVerify.Core/Canonical/Canonicalizer.cs` (reference),
2. `src/Ora2PgVerify.Core/Sql/OracleDialect.cs`,
3. `src/Ora2PgVerify.Core/Sql/PostgresDialect.cs`.

Change all three, add awkward values to `CanonicalParityTests` (integration) and a property to
`CanonicalPropertyTests` if an invariant is involved, then re-accept the SQL golden files with
`UPDATE_GOLDEN=1 dotnet test --project tests/Ora2PgVerify.UnitTests` and review the diff.

## Adding a scan rule

Add one entry to `ScanRules.All` (id, title, severity, category, suggestion, reference, token matcher), a case
to `ScannerTests.Each_rule_fires_on_its_construct`, and, if the sample schema should show it, a line to
`src/Ora2PgVerify.Oracle/Schema/plsql.sql` (then re-accept `scan-sample.txt`).

## Benchmarks

Numbers in the README come from `ora2pg-verify bench` on a seeded 1 M-row database (docs/benchmarks.md).
Re-run them when the hot path changes and update both files with the new medians and host details.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) keep the history readable and make the
hand-written CHANGELOG easy to maintain.
