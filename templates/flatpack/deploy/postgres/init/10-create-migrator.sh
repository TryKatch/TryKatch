#!/usr/bin/env bash
set -Eeuo pipefail

if [[ -z "${FLATPACK_MIGRATOR_PASSWORD:-}" ]]; then
  echo "FLATPACK_MIGRATOR_PASSWORD is required" >&2
  exit 1
fi

psql --quiet --set ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --set database_name="$POSTGRES_DB" \
  --set migrator_password="$FLATPACK_MIGRATOR_PASSWORD" <<'EOSQL'
SELECT format(
  'CREATE ROLE flatpack_migrator WITH LOGIN PASSWORD %L NOINHERIT NOBYPASSRLS',
  :'migrator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flatpack_migrator') \gexec

SELECT format(
  'ALTER ROLE flatpack_migrator WITH LOGIN PASSWORD %L NOINHERIT NOBYPASSRLS',
  :'migrator_password') \gexec

ALTER DATABASE :"database_name" OWNER TO flatpack_migrator;
EOSQL
