Measured 2026-11-11 13:48 UTC on Microsoft Windows 10.0.26200; X64; 16 logical CPUs; .NET 10.0.12.

Host: i7-13620H laptop (16 logical CPUs, 15.7 GB RAM), Windows 11, Docker Desktop VM 7.6 GB; CLI on the host, Oracle 23ai Free and PostgreSQL 17 in containers (oracle-free ~2.7 GB RSS); only this project's 3 containers running

Correction added after the run: another project's container (postgres:17-alpine, about 3.6 CPUs at peak) started at 13:43:29 UTC, during this run. The strategy rows ran before it; the hash and chunk rows may overlap it. The chunk sweep was repeated on a quiet host (foreign CPU below 4%) in [bench-chunk.md](bench-chunk.md), which supersedes the chunk rows below.

| group | configuration | table | rows | runs | median ms | min | max | fingerprint ms | drill-down ms | Oracle busy ms | PostgreSQL busy ms | queries | rows transferred | differing rows |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| strategy | database-side, MD5, 10k-row chunks | orders | 999,995 | 5 | 8,992 | 8,594 | 9,129 | 5,521 | 3,427 | 8,678 | 6,043 | 104 | 83,068 | 70 |
| strategy | client-side, MD5 | orders | 999,995 | 5 | 13,774 | 13,154 | 16,114 | 13,727 | 0 | 13,758 | 13,279 | 6 | 2,000,092 | 70 |
| strategy | database-side, MD5, 10k-row chunks | customers | 50,000 | 5 | 645 | 560 | 964 | 262 | 364 | 524 | 554 | 8 | 100,022 | 20 |
| strategy | client-side, MD5 | customers | 50,000 | 5 | 577 | 503 | 693 | 560 | 0 | 511 | 373 | 6 | 100,012 | 20 |
| strategy | database-side, MD5, 10k-row chunks | order_documents | 20,000 | 5 | 1,631 | 1,436 | 1,936 | 648 | 962 | 1,557 | 1,223 | 8 | 40,026 | 10 |
| strategy | client-side, MD5 | order_documents | 20,000 | 5 | 2,984 | 2,711 | 3,141 | 2,962 | 0 | 2,961 | 2,296 | 6 | 40,022 | 10 |
| hash | database-side, SHA-256, 10k-row chunks | orders | 999,995 | 5 | 9,885 | 9,705 | 10,594 | 6,129 | 3,709 | 9,795 | 6,549 | 104 | 83,068 | 70 |
| chunk | database-side, 1,000-row chunks | orders | 999,995 | 3 | 6,450 | 6,019 | 6,843 | 5,682 | 713 | 6,373 | 3,183 | 136 | 12,276 | 70 |
| chunk | database-side, 10,000-row chunks | orders | 999,995 | 3 | 12,973 | 12,958 | 13,499 | 8,019 | 4,889 | 12,848 | 9,061 | 104 | 83,068 | 70 |
| chunk | database-side, 100,000-row chunks | orders | 999,995 | 3 | 24,329 | 21,831 | 25,638 | 9,403 | 14,818 | 24,141 | 15,471 | 146 | 53,868 | 70 |
| chunk | database-side, 1,000,000-row chunks | orders | 999,995 | 3 | 29,320 | 20,661 | 33,891 | 9,881 | 19,331 | 29,266 | 18,211 | 162 | 34,766 | 70 |
| chunk | database-side, 100-row buckets | customers | 50,000 | 3 | 387 | 372 | 427 | 295 | 67 | 337 | 375 | 8 | 4,646 | 20 |
| chunk | database-side, 1,000-row buckets | customers | 50,000 | 3 | 470 | 449 | 491 | 299 | 152 | 375 | 452 | 8 | 34,366 | 20 |
| chunk | database-side, 10,000-row buckets | customers | 50,000 | 3 | 658 | 622 | 877 | 260 | 372 | 601 | 581 | 8 | 100,022 | 20 |

PL/SQL scan: median 47 ms, 60 findings.

| defect | table | injected | detected | exact column | classified |
|---|---|---:|---:|---:|---:|
| CUS_CASE | customers | 10 | 10 | 10 | 10 |
| CUS_MISSING | customers | 10 | 10 | 10 | 10 |
| DOC_CLOB_EDIT | order_documents | 10 | 10 | 10 | 10 |
| ORD_AMOUNT_DRIFT | orders | 10 | 10 | 10 | 10 |
| ORD_EMPTY_NOTE | orders | 10 | 10 | 10 | 10 |
| ORD_EXTRA | orders | 10 | 10 | 10 | 10 |
| ORD_MISSING | orders | 10 | 10 | 10 | 10 |
| ORD_TIME_TRUNCATED | orders | 10 | 10 | 10 | 10 |
| ORD_TRAILING_SPACE | orders | 10 | 10 | 10 | 10 |
| ORD_TZ_SHIFT | orders | 10 | 10 | 10 | 10 |

Recall 100.0 %, precision 100.0 %, false positives 0, missed 0.
