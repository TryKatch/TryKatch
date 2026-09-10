#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
compose_file="$repository_root/templates/trykatch/compose.yml"
export COMPOSE_PROJECT_NAME=trykatch_migrator_test
export TRYKATCH_RELEASE_VERSION=ci-validation
export TRYKATCH_POSTGRES_ADMIN_PASSWORD=postgres-admin-test-password
export TRYKATCH_MIGRATOR_PASSWORD=migrator-test-password-with-24-characters
export TRYKATCH_ORG_RUNTIME_PASSWORD=org-runtime-test-password-with-24-characters
export TRYKATCH_PLATFORM_RUNTIME_PASSWORD=platform-runtime-test-password-with-24-characters
export TRYKATCH_IDENTITY_RUNTIME_PASSWORD=identity-runtime-test-password-with-24-characters
export TRYKATCH_OUTBOX_WORKER_PASSWORD=outbox-runtime-test-password-with-24-characters
export TRYKATCH_MIGRATOR_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_migrator;Password=$TRYKATCH_MIGRATOR_PASSWORD"
export TRYKATCH_ORG_RUNTIME_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_org_runtime;Password=$TRYKATCH_ORG_RUNTIME_PASSWORD"
export TRYKATCH_PLATFORM_RUNTIME_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_platform_runtime;Password=$TRYKATCH_PLATFORM_RUNTIME_PASSWORD"
export TRYKATCH_IDENTITY_RUNTIME_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_identity_runtime;Password=$TRYKATCH_IDENTITY_RUNTIME_PASSWORD"
export TRYKATCH_OUTBOX_WORKER_CONNECTION="Host=postgres;Port=5432;Database=trykatch;Username=trykatch_outbox_worker;Password=$TRYKATCH_OUTBOX_WORKER_PASSWORD"
export GRAFANA_ADMIN_PASSWORD=grafana-admin-test-password

cleanup() {
  docker compose -f "$compose_file" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

assert_equal() {
  local label=$1
  local expected=$2
  local actual=$3
  if [[ "$actual" != "$expected" ]]; then
    printf '%s mismatch\nexpected: %s\nactual:   %s\n' "$label" "$expected" "$actual" >&2
    return 1
  fi
}

docker compose -f "$compose_file" up --detach --wait postgres
docker compose -f "$compose_file" run --rm --build migrator

role_state=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname trykatch \
  --command="SELECT string_agg(r.rolname || ':' || r.rolsuper || ':' || r.rolbypassrls || ':' || owned.relations, ',' ORDER BY r.rolname) FROM pg_roles r CROSS JOIN LATERAL (SELECT count(*) AS relations FROM pg_class c WHERE c.relowner = r.oid) owned WHERE r.rolname IN ('trykatch_org_runtime', 'trykatch_platform_runtime', 'trykatch_identity_runtime', 'trykatch_outbox_worker');")
assert_equal "runtime role state" \
  "trykatch_identity_runtime:false:false:0,trykatch_org_runtime:false:false:0,trykatch_outbox_worker:false:false:0,trykatch_platform_runtime:false:false:0" \
  "$role_state"

schema_count=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname trykatch \
  --command="SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('identity', 'platform', 'app');")
assert_equal "managed schema count" "3" "$schema_count"

privilege_state=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname trykatch \
  --command="SELECT concat_ws(',', has_table_privilege('trykatch_org_runtime', 'app.projects', 'SELECT'), has_table_privilege('trykatch_org_runtime', 'platform.organizations', 'SELECT'), has_table_privilege('trykatch_platform_runtime', 'platform.organizations', 'SELECT'), has_table_privilege('trykatch_platform_runtime', 'platform.outbox_messages', 'SELECT'), has_table_privilege('trykatch_identity_runtime', 'identity.\"AspNetUsers\"', 'SELECT'), has_table_privilege('trykatch_identity_runtime', 'app.projects', 'SELECT'), has_table_privilege('trykatch_outbox_worker', 'platform.outbox_messages', 'UPDATE'), has_table_privilege('trykatch_outbox_worker', 'app.projects', 'SELECT'));" )
assert_equal "runtime privilege state" "t,t,t,f,t,f,t,f" "$privilege_state"

public_acl_count=$(docker compose -f "$compose_file" exec -T postgres \
  psql --tuples-only --no-align --username postgres --dbname trykatch \
  --command="SELECT (SELECT count(*) FROM pg_namespace n CROSS JOIN LATERAL aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) a WHERE n.nspname IN ('app','platform','identity','reference','infrastructure','public') AND a.grantee = 0) + (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace CROSS JOIN LATERAL aclexplode(coalesce(c.relacl, acldefault(CASE WHEN c.relkind = 'S' THEN 'S'::\"char\" ELSE 'r'::\"char\" END, c.relowner))) a WHERE n.nspname IN ('app','platform','identity','reference','infrastructure','public') AND a.grantee = 0) + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace CROSS JOIN LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a WHERE n.nspname IN ('app','platform','identity','reference','infrastructure','public') AND a.grantee = 0);" )
assert_equal "PUBLIC ACL count" "0" "$public_acl_count"
