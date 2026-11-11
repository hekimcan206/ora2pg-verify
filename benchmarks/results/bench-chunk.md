Measured 2026-11-11 13:54 UTC on Microsoft Windows 10.0.26200; X64; 16 logical CPUs; .NET 10.0.12.

Host: same host; foreign containers' CPU sampled every 15 s during the run, see docs/benchmarks.md

| group | configuration | table | rows | runs | median ms | min | max | fingerprint ms | drill-down ms | Oracle busy ms | PostgreSQL busy ms | queries | rows transferred | differing rows |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| chunk | database-side, 500-row chunks | orders | 999,995 | 3 | 5,585 | 5,580 | 5,623 | 5,273 | 270 | 5,508 | 2,441 | 8 | 69,224 | 70 |
| chunk | database-side, 1,000-row chunks | orders | 999,995 | 3 | 6,502 | 6,332 | 7,192 | 5,775 | 676 | 6,471 | 3,129 | 136 | 12,276 | 70 |
| chunk | database-side, 2,000-row chunks | orders | 999,995 | 3 | 6,612 | 6,197 | 7,570 | 5,726 | 806 | 6,553 | 3,876 | 134 | 19,520 | 70 |
| chunk | database-side, 5,000-row chunks | orders | 999,995 | 3 | 6,834 | 6,792 | 6,968 | 5,111 | 1,682 | 6,786 | 4,657 | 126 | 43,572 | 70 |
| chunk | database-side, 10,000-row chunks | orders | 999,995 | 3 | 8,150 | 7,957 | 8,263 | 5,233 | 2,877 | 8,105 | 5,859 | 104 | 83,068 | 70 |
| chunk | database-side, 100,000-row chunks | orders | 999,995 | 3 | 12,371 | 12,167 | 13,013 | 5,166 | 7,168 | 12,324 | 8,244 | 146 | 53,868 | 70 |
| chunk | database-side, 1,000,000-row chunks | orders | 999,995 | 3 | 16,537 | 16,312 | 16,611 | 5,067 | 11,428 | 16,502 | 10,395 | 162 | 34,766 | 70 |
| chunk | database-side, 100-row buckets | customers | 50,000 | 3 | 278 | 255 | 280 | 215 | 52 | 230 | 275 | 8 | 4,646 | 20 |
| chunk | database-side, 1,000-row buckets | customers | 50,000 | 3 | 320 | 312 | 336 | 195 | 111 | 292 | 306 | 8 | 34,366 | 20 |
| chunk | database-side, 10,000-row buckets | customers | 50,000 | 3 | 456 | 434 | 469 | 215 | 229 | 389 | 438 | 8 | 100,022 | 20 |

PL/SQL scan: median 25 ms, 60 findings.
