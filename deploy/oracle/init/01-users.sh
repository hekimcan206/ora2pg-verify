#!/bin/bash
# Runs once on the first start of gvenzl/oracle-free, as the oracle OS user (also mounted by the Testcontainers fixtures).
#   APP_OWNER      owns the synthetic source schema; used only by `seed` and by the simulated application (dual-write demo)
#   VERIFY_READER  the account ora2pg-verify compares and scans with:
#                    CREATE SESSION + SELECT on the owner's tables (through role VERIFY_READ, granted by `seed`)
#                    SELECT_CATALOG_ROLE   read-only dictionary access (DBA_SOURCE etc.) for the PL/SQL scan; no table data
#                    EXECUTE ON DBMS_CRYPTO  CLOB digests (STANDARD_HASH refuses LOBs)
#                  no write privilege of any kind; the comparison also runs inside SET TRANSACTION READ ONLY.
set -euo pipefail
: "${OWNER_PASSWORD:?OWNER_PASSWORD must be set}"
: "${READER_PASSWORD:?READER_PASSWORD must be set}"

sqlplus -s / as sysdba <<SQL
WHENEVER SQLERROR EXIT SQL.SQLCODE
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER app_owner IDENTIFIED BY "${OWNER_PASSWORD}" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users;
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE SEQUENCE, CREATE SYNONYM, CREATE TRIGGER, CREATE TYPE TO app_owner;
GRANT EXECUTE ON SYS.DBMS_CRYPTO TO app_owner;

CREATE ROLE verify_read;
CREATE USER verify_reader IDENTIFIED BY "${READER_PASSWORD}";
GRANT CREATE SESSION, verify_read, SELECT_CATALOG_ROLE TO verify_reader;
GRANT EXECUTE ON SYS.DBMS_CRYPTO TO verify_reader;
ALTER USER verify_reader DEFAULT ROLE ALL;
EXIT
SQL
