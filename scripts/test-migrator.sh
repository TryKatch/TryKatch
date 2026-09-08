#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
compose_file="$repository_root/templates/flatpack/compose.yml"
export COMPOSE_PROJECT_NAME=flatpack_migrator_test
export FLATPACK_POSTGRES_ADMIN_PASSWORD=postgres-admin-test-password
export FLATPACK_MIGRATOR_PASSWORD=migrator-test-password-with-24-characters
export FLATPACK_RUNTIME_PASSWORD=runtime-test-password-with-24-characters
export FLATPACK_MIGRATOR_CONNECTION="Host=postgres;Port=5432;Database=flatpack;Username=flatpack_migrator;Password=$FLATPACK_MIGRATOR_PASSWORD"
export FLATPACK_RUNTIME_CONNECTION="Host=postgres;Port=5432;Database=flatpack;Username=flatpack_runtime;Password=$FLATPACK_RUNTIME_PASSWORD"
export GRAFANA_ADMIN_PASSWORD=grafana-admin-test-password

cleanup() {
  docker compose -f "$compose_file" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker compose -f "$compose_file" up --detach --wait postgres
docker compose -f "$compose_file" run --rm --build migrator

role_state=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname flatpack \
  --command="SELECT rolsuper, rolbypassrls, count(c.oid) FROM pg_roles r LEFT JOIN pg_class c ON c.relowner = r.oid WHERE r.rolname = 'flatpack_runtime' GROUP BY r.rolsuper, r.rolbypassrls;")
test "$role_state" = "f|f|0"

schema_count=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname flatpack \
  --command="SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('identity', 'platform', 'app');")
test "$schema_count" = "3"
