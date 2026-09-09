#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
compose_file="$repository_root/templates/trykatch/compose.yml"
export COMPOSE_PROJECT_NAME=trykatch_migrator_test
export TRYKATCH_POSTGRES_ADMIN_PASSWORD=postgres-admin-test-password
export TRYKATCH_MIGRATOR_PASSWORD=migrator-test-password-with-24-characters
export TRYKATCH_RUNTIME_PASSWORD=runtime-test-password-with-24-characters
export TRYKATCH_MIGRATOR_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_migrator;Password=$TRYKATCH_MIGRATOR_PASSWORD"
export TRYKATCH_RUNTIME_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_runtime;Password=$TRYKATCH_RUNTIME_PASSWORD"
export GRAFANA_ADMIN_PASSWORD=grafana-admin-test-password

cleanup() {
  docker compose -f "$compose_file" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker compose -f "$compose_file" up --detach --wait postgres
docker compose -f "$compose_file" run --rm --build migrator

role_state=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname trykatch \
  --command="SELECT rolsuper, rolbypassrls, count(c.oid) FROM pg_roles r LEFT JOIN pg_class c ON c.relowner = r.oid WHERE r.rolname = 'trykatch_runtime' GROUP BY r.rolsuper, r.rolbypassrls;")
test "$role_state" = "f|f|0"

schema_count=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname trykatch \
  --command="SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('identity', 'platform', 'app');")
test "$schema_count" = "3"
