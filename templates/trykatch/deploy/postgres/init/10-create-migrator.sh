#!/usr/bin/env bash
set -Eeuo pipefail

if [[ -z "${TRYKATCH_MIGRATOR_PASSWORD:-}" ]]; then
  echo "TRYKATCH_MIGRATOR_PASSWORD is required" >&2
  exit 1
fi
for role_secret in TRYKATCH_ORG_RUNTIME_PASSWORD TRYKATCH_PLATFORM_RUNTIME_PASSWORD TRYKATCH_IDENTITY_RUNTIME_PASSWORD TRYKATCH_OUTBOX_WORKER_PASSWORD; do
  if [[ -z "${!role_secret:-}" ]]; then
    echo "$role_secret is required" >&2
    exit 1
  fi
done

psql --quiet --set ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --set database_name="$POSTGRES_DB" \
  --set migrator_password="$TRYKATCH_MIGRATOR_PASSWORD" \
  --set org_runtime_password="$TRYKATCH_ORG_RUNTIME_PASSWORD" \
  --set platform_runtime_password="$TRYKATCH_PLATFORM_RUNTIME_PASSWORD" \
  --set identity_runtime_password="$TRYKATCH_IDENTITY_RUNTIME_PASSWORD" \
  --set outbox_worker_password="$TRYKATCH_OUTBOX_WORKER_PASSWORD" <<'EOSQL'
SELECT format(
  'CREATE ROLE trykatch_migrator WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
  :'migrator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_migrator') \gexec

SELECT format(
  'ALTER ROLE trykatch_migrator WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
  :'migrator_password') \gexec

SELECT format('CREATE ROLE trykatch_org_runtime WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'org_runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_org_runtime') \gexec
SELECT format('ALTER ROLE trykatch_org_runtime WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'org_runtime_password') \gexec

SELECT format('CREATE ROLE trykatch_platform_runtime WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'platform_runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_platform_runtime') \gexec
SELECT format('ALTER ROLE trykatch_platform_runtime WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'platform_runtime_password') \gexec

SELECT format('CREATE ROLE trykatch_identity_runtime WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'identity_runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_identity_runtime') \gexec
SELECT format('ALTER ROLE trykatch_identity_runtime WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'identity_runtime_password') \gexec

SELECT format('CREATE ROLE trykatch_outbox_worker WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'outbox_worker_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_outbox_worker') \gexec
SELECT format('ALTER ROLE trykatch_outbox_worker WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'outbox_worker_password') \gexec

ALTER DATABASE :"database_name" OWNER TO trykatch_migrator;
EOSQL
