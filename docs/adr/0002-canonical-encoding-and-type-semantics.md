# 2. One canonical encoding, implemented three times and proven equal

- Status: accepted
- Date: 2026-11-11

## Context

A checksum comparison is only as good as the agreement between the two sides on what a value *is*.
Oracle and PostgreSQL disagree in ways that are well known and still catch migrations out:

| Topic | Oracle | PostgreSQL |
|---|---|---|
| Empty string | `''` **is** NULL ([AWS: empty strings][aws-empty]) | `''` is a value distinct from NULL |
| `DATE` | date **and** time of day, second precision | `date` has no time; Oracle `DATE` maps to `timestamp(0)` ([AWS date functions][aws-date]) |
| `NUMBER` without precision | exact decimal, up to 38 digits | ora2pg picks `bigint`/`numeric` via `PG_INTEGER_TYPE`/`DEFAULT_NUMERIC`, other tools default to `double precision` ([ora2pg type control][ora2pg-types], [AWS NUMBER conversion][aws-number]) |
| Number text | `TO_CHAR(0.5)` = `.5` | `0.5`, and `numeric(14,2)` prints `12.50` |
| `TIMESTAMP WITH TIME ZONE` | keeps the offset or region | `timestamptz` stores the instant only |
| `CHAR(n)` | blank-padded, PAD SPACE comparison | blank-padded, but trailing blanks are dropped on cast to text ([pgsql-hackers on CHAR semantics][char]) |
| `CLOB` | `STANDARD_HASH` refuses LOBs (verified: ORA-00902) | `text`, `md5()` works on any length |
| Text order | `NLS_SORT` (binary by default) | database collation (libc/ICU) |
| String length in SQL | `VARCHAR2` row strings stop at 4000 bytes (`MAX_STRING_SIZE=STANDARD`) | 1 GB |

## Options considered

1. **Hash each side's native text representation.** Every row would differ on the first `12.50`.
2. **Normalise in the client only** (fetch, convert, hash). This is correct but forfeits pushdown (ADR 0001).
3. **Define one canonical encoding and implement it in Oracle SQL, PostgreSQL SQL and C#, with a test
   that the three agree on adversarial values.**

## Decision

Option 3. The C# `Canonicalizer` is the reference. `OracleDialect` and `PostgresDialect` generate the SQL.

| Kind (from the Oracle type) | Canonical text | Oracle SQL | PostgreSQL SQL |
|---|---|---|---|
| NUMBER | shortest exact decimal, no trailing zeros, leading `0.` | `TO_CHAR(n,'TM9', nls)` + restore leading zero (skipped for integer columns) | `trim_scale(x::numeric)::text` (floats go through numeric, 15 significant digits, so lost digits **show**) |
| DATE | `YYYY-MM-DDTHH:MI:SS`, plus `.ffffff` only if the target has a fraction | `TO_CHAR(d, …)` | `to_char(…)` + fraction if `date_trunc('second', x) <> x` |
| TIMESTAMP | microseconds | `FF6` | `.US` |
| TIMESTAMP WITH TIME ZONE | UTC instant | `SYS_EXTRACT_UTC` | `AT TIME ZONE 'UTC'` |
| VARCHAR2 | bytes as stored, trailing spaces significant | column | `col::text`, or `NULLIF(col,'')` with `--empty-as-null` |
| CHAR | trailing blanks removed; all blanks = NULL (Oracle's `RTRIM('  ')` is NULL) | `RTRIM(c)` | `NULLIF(rtrim(c),'')` |
| CLOB, VARCHAR2 > 1000 bytes | hex digest of UTF-8 text | `DBMS_CRYPTO.HASH(clob, MD5/SH256)` (converts to AL32UTF8, verified equal to PostgreSQL `md5(text)`) or `STANDARD_HASH` | `md5(text)` / `sha256(convert_to(text,'UTF8'))` |

Each field is encoded as `~` (NULL) or `<UTF-8 byte length>:<text>`. The length prefix makes the
concatenation **injective** without escaping: `('a','bc')` and `('ab','c')` differ, NULL differs from `''`,
and a value that *is* `~` cannot be mistaken for NULL. Byte length (`LENGTHB` / `octet_length`) is used rather
than character count because the engines count supplementary characters differently in some configurations.

Empty strings default to **reported** (`EmptyStringPolicy.Distinct`). PostgreSQL holding `''` where Oracle
could only hold NULL changes behaviour: `WHERE note IS NULL` stops finding the row. `--empty-as-null`
accepts it for teams that have fixed their code.

Text and composite keys use hash buckets (ADR 0001), so no comparison depends on collation.

## What is pushed down and what is not

| Pushed down to both engines | Not pushed down |
|---|---|
| number / date / timestamp / tz / text / CHAR canonicalisation, row digest, 60-bit fingerprint, chunk and bucket arithmetic, CLOB digest | `BLOB`, `RAW`, `LONG`, `BFILE`, `XMLTYPE`, `INTERVAL`, `BINARY_FLOAT/DOUBLE` on the Oracle side: reported as `TYPE_NOT_COMPARED` and excluded (no silent pass) |
| | NCLOB / NVARCHAR2 in a non-AL32UTF8 national character set: not verified |
| | Oracle databases whose character set is not AL32UTF8: `STANDARD_HASH` hashes database-charset bytes; a `CONVERT(…,'AL32UTF8')` wrapper would be needed (not implemented) |
| | Dates before year 1 or after 9999, `NUMBER` values that `TO_CHAR(…,'TM9')` writes in scientific notation (beyond 64 characters) |

## Consequences

- The integration suite inserts 300 rows of awkward values (negative fractions, 27-digit numbers,
  midnight and non-midnight DATEs, offsets from −12:00 to +14:00, Turkish and 4-byte UTF-8 text, leading and
  trailing blanks, all-blank CHAR, multi-kilobyte CLOBs, NULLs everywhere) into both engines. It asserts that
  Oracle SQL, PostgreSQL SQL and C# produce the same field for every value, with both empty-string policies
  and both hash algorithms.
- The generated SQL is pinned by golden files, so any change to it shows up in review.
- Representation differences that do not change meaning (CHAR padding not stored, offsets not stored,
  `12.50` vs `12.5`) never produce findings. The synthetic schema has them on every row, and precision on the
  1 M-row run is 100%.

[aws-empty]: https://aws.amazon.com/blogs/database/handle-empty-strings-when-migrating-from-oracle-to-postgresql/
[aws-date]: https://aws.amazon.com/blogs/database/migrate-date-functions-from-oracle-to-amazon-rds-for-postgresql
[ora2pg-types]: https://ora2pg.darold.net/docs/configuration/type-control
[aws-number]: https://aws.amazon.com/blogs/database/convert-the-number-data-type-from-oracle-to-postgresql-part-2
[char]: https://www.postgresql.org/message-id/CAK%2BWP1x5NOXXoh36cEcT%3DJRZ%3D_a7%3D53cb66TfAzMM9DnzFqURw%40mail.gmail.com
