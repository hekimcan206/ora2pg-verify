# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability**). Expect an acknowledgement within 5 working days and a fix or mitigation
plan within 30 days of confirmation.

The most valuable reports are: a way to make the tool write to a database through the reader account, a
credential or row value appearing in a report or log when it should not, or SQL injection through catalog
identifiers. See [docs/threat-model.md](docs/threat-model.md).

## Data

This project contains **synthetic data only**. Never attach real reports, connection strings, schemas or query
results from real databases to issues or pull requests.

## Security-relevant defaults

| Area | Default |
|---|---|
| Credentials | Read from environment variables; never written to reports or logs |
| Database accounts | Read-only accounts for scan/compare/audit (grants + read-only transaction); owner accounts only for the synthetic seed and the dual-write simulator |
| Report content | No row values unless `--include-values`; CLOBs as digests; literals redacted in code snippets; self-contained HTML, no scripts |
| SQL | Catalog identifiers validated and quoted, everything else bound |
| Container | Chiseled runtime image, non-root UID 1654, no shell |
| Supply chain | Central, pinned package versions updated by hand; vulnerable transitive `System.Security.Cryptography.Pkcs` pinned to a patched version; CI fails on known-vulnerable packages (`dotnet list package --vulnerable --include-transitive`); CodeQL `security-and-quality` |
