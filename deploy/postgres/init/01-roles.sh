#!/bin/sh
# Runs once on the first start of postgres:17-alpine (also mounted by the Testcontainers fixtures).
#   app_owner      owns schema "app"; used only by `seed` and by the simulated application (dual-write demo)
#   verify_reader  the account ora2pg-verify compares with: CONNECT + USAGE + SELECT, read-only by default
#                  (default_transaction_read_only), and the comparison runs in REPEATABLE READ READ ONLY.
set -eu
: "${OWNER_PASSWORD:?OWNER_PASSWORD must be set}"
: "${READER_PASSWORD:?READER_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
CREATE ROLE app_owner LOGIN PASSWORD '${OWNER_PASSWORD}';
CREATE ROLE verify_reader LOGIN PASSWORD '${READER_PASSWORD}';
ALTER ROLE verify_reader SET default_transaction_read_only = on;
ALTER ROLE verify_reader SET statement_timeout = '15min';

REVOKE ALL ON DATABASE "$POSTGRES_DB" FROM PUBLIC;
GRANT CONNECT ON DATABASE "$POSTGRES_DB" TO app_owner, verify_reader;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA app AUTHORIZATION app_owner;
GRANT USAGE ON SCHEMA app TO verify_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA app GRANT SELECT ON TABLES TO verify_reader;
SQL
