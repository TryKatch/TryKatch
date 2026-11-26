#!/usr/bin/env bash
set -euo pipefail

# Rehearse an EF10 -> EF11 production database upgrade across PostgreSQL 18 -> 19.
# Both database servers are isolated disposable containers; this script never uses
# compose volumes or any configured application database. Set EF10_MIGRATOR_DLL
# to the previous release's real published migrator before invoking this script.

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
ef10_migrator_dll=${EF10_MIGRATOR_DLL:-}
ef11_migrator_dll=${EF11_MIGRATOR_DLL:-$repository_root/templates/trykatch/src/API/Trykatch.Migrator/bin/Release/net11.0/Trykatch.Migrator.dll}
postgres18_image=${POSTGRES18_IMAGE:-postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f}
postgres19_image=${POSTGRES19_IMAGE:-postgres:19.0-alpine3.23@sha256:40ad7e457013448b5522c6c5d30dccb8248959c41adc0e56c1785be4293bbd67}

for executable in docker dotnet; do
  command -v "$executable" >/dev/null || { printf 'Required executable not found: %s\n' "$executable" >&2; exit 1; }
done
[[ -n "$ef10_migrator_dll" && -f "$ef10_migrator_dll" ]] || {
  printf 'Set EF10_MIGRATOR_DLL to the previous release migrator DLL.\n' >&2
  exit 1
}
[[ -f "$ef11_migrator_dll" ]] || { printf 'Migrator DLL not found: %s\n' "$ef11_migrator_dll" >&2; exit 1; }
ef10_deps_json=${ef10_migrator_dll%.dll}.deps.json
ef11_deps_json=${ef11_migrator_dll%.dll}.deps.json
[[ -f "$ef10_deps_json" ]] || {
  printf 'Set EF10_MIGRATOR_DLL to the previous release migrator DLL (its .deps.json must be alongside it).\n' >&2
  exit 1
}
[[ -f "$ef11_deps_json" ]] || { printf 'Candidate migrator .deps.json not found beside %s.\n' "$ef11_migrator_dll" >&2; exit 1; }
rg -q '"Microsoft\.EntityFrameworkCore/10\.' "$ef10_deps_json" || {
  printf 'EF10_MIGRATOR_DLL does not declare Entity Framework Core major version 10: %s\n' "$ef10_migrator_dll" >&2
  exit 1
}
rg -q '"Microsoft\.EntityFrameworkCore/11\.' "$ef11_deps_json" || {
  printf 'EF11_MIGRATOR_DLL does not declare Entity Framework Core major version 11: %s\n' "$ef11_migrator_dll" >&2
  exit 1
}

temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-postgres-upgrade.XXXXXX")
run_id="${USER:-user}-$$-${RANDOM}"
pg18_name="trykatch-pg18-upgrade-${run_id}"
pg19_name="trykatch-pg19-upgrade-${run_id}"
pg_admin_password="admin-${RANDOM}-upgrade-test"
migrator_password="migrator-${RANDOM}-upgrade-test"
org_runtime_password="org-${RANDOM}-upgrade-test"
platform_runtime_password="platform-${RANDOM}-upgrade-test"
identity_runtime_password="identity-${RANDOM}-upgrade-test"
outbox_runtime_password="outbox-${RANDOM}-upgrade-test"
work_runtime_password="work-${RANDOM}-upgrade-test"

cleanup() {
  docker rm -f "$pg18_name" "$pg19_name" >/dev/null 2>&1 || true
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

assert_equal() {
  local label=$1 expected=$2 actual=$3
  if [[ "$expected" != "$actual" ]]; then
    printf '%s mismatch\nexpected: %s\nactual:   %s\n' "$label" "$expected" "$actual" >&2
    return 1
  fi
}

start_postgres() {
  local name=$1 image=$2
  docker run --detach --rm --name "$name" \
    --publish 127.0.0.1::5432 \
    --env POSTGRES_PASSWORD="$pg_admin_password" \
    --health-cmd='pg_isready -U postgres -d postgres' \
    --health-interval=1s --health-timeout=3s --health-retries=60 \
    "$image" >/dev/null
  local status
  for _ in $(seq 1 90); do
    status=$(docker inspect --format '{{.State.Health.Status}}' "$name")
    [[ "$status" == healthy ]] && return
    [[ "$status" == unhealthy ]] && { docker logs "$name" >&2; return 1; }
    sleep 1
  done
  docker logs "$name" >&2
  printf 'Timed out waiting for %s to become healthy.\n' "$name" >&2
  return 1
}

container_port() {
  docker port "$1" 5432/tcp | sed 's/.*://'
}

psql_admin() {
  local name=$1
  shift
  docker exec --interactive --env PGPASSWORD="$pg_admin_password" "$name" \
    psql --no-psqlrc --set ON_ERROR_STOP=1 --username postgres --dbname postgres "$@"
}

psql_container() {
  local name=$1 username=$2 database=$3 password=$4
  shift 4
  docker exec --interactive --env PGPASSWORD="$password" "$name" \
    psql --no-psqlrc --set ON_ERROR_STOP=1 --username "$username" --dbname "$database" "$@"
}

database_connection() {
  local port=$1 username=$2 password=$3
  printf 'Host=127.0.0.1;Port=%s;Database=trykatch;Username=%s;Password=%s;Pooling=false' \
    "$port" "$username" "$password"
}

run_migrator() {
  local dll=$1 port=$2
  printf 'Running %s against disposable PostgreSQL on port %s.\n' "$(basename "$dll")" "$port"
  env \
    ConnectionStrings__trykatchdb="$(database_connection "$port" trykatch_migrator "$migrator_password")" \
    Database__OrganizationRuntimeRole=trykatch_org_runtime \
    Database__PlatformRuntimeRole=trykatch_platform_runtime \
    Database__IdentityRuntimeRole=trykatch_identity_runtime \
    Database__OutboxWorkerRole=trykatch_outbox_worker \
    Database__WorkCoordinatorRole=trykatch_work_coordinator \
    DOTNET_ROLL_FORWARD=Major \
    dotnet "$dll"
}

provision_roles_and_database() {
  local name=$1
  psql_admin "$name" --set migrator_password="$migrator_password" \
    --set org_password="$org_runtime_password" --set platform_password="$platform_runtime_password" \
    --set identity_password="$identity_runtime_password" --set outbox_password="$outbox_runtime_password" \
    --set work_password="$work_runtime_password" <<'SQL'
CREATE ROLE trykatch_migrator LOGIN PASSWORD :'migrator_password' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE trykatch_org_runtime LOGIN PASSWORD :'org_password' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE trykatch_platform_runtime LOGIN PASSWORD :'platform_password' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE trykatch_identity_runtime LOGIN PASSWORD :'identity_password' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE trykatch_outbox_worker LOGIN PASSWORD :'outbox_password' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE trykatch_work_coordinator LOGIN PASSWORD :'work_password' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE DATABASE trykatch OWNER trykatch_migrator;
SQL
}

query_on_port() {
  local port=$1 username=$2 password=$3 sql=$4
  printf '%s\n' "$sql" | PGPASSWORD="$password" psql --no-psqlrc --quiet --tuples-only --no-align \
    --set ON_ERROR_STOP=1 --host 127.0.0.1 --port "$port" --username "$username" \
    --dbname trykatch --file=-
}

server_version() {
  query_on_port "$1" trykatch_migrator "$migrator_password" 'SHOW server_version_num;'
}

ef_history_count() {
  query_on_port "$1" trykatch_migrator "$migrator_password" 'SELECT count(*) FROM public."__EFMigrationsHistory";'
}

module_history_count() {
  query_on_port "$1" trykatch_migrator "$migrator_password" 'SELECT count(*) FROM platform.module_migrations;'
}

stable_row_snapshot() {
  local port=$1
  query_on_port "$port" postgres "$pg_admin_password" \
    "SELECT 'organizations:' || count(*) || ':' || md5(coalesce(string_agg(concat_ws('|', \"Id\", \"Name\", \"Slug\", \"IsActive\", \"CreatedAt\"), E'\\n' ORDER BY \"Id\"), '')) FROM platform.organizations; SELECT 'projects:' || count(*) || ':' || md5(coalesce(string_agg(concat_ws('|', \"Id\", \"OrganizationId\", \"Name\", \"Description\", \"CreatedBy\", \"CreatedAt\"), E'\\n' ORDER BY \"Id\"), '')) FROM app.projects;"
}

ef_history_snapshot() {
  local port=$1
  query_on_port "$port" trykatch_migrator "$migrator_password" \
    "SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\";"
}

module_history_snapshot() {
  local port=$1
  query_on_port "$port" trykatch_migrator "$migrator_password" \
    "SELECT concat_ws('|', module_id, migration_id, checksum) FROM platform.module_migrations ORDER BY module_id, migration_id;"
}

role_ownership_snapshot() {
  local name=$1
  psql_admin "$name" --dbname trykatch --tuples-only --no-align --command="
    SELECT string_agg(rolname || ':' || rolsuper || ':' || rolcreatedb || ':' || rolcreaterole || ':' || rolreplication || ':' || rolbypassrls || ':' || rolinherit || ':' || owned.object_count, ',' ORDER BY rolname)
    FROM pg_roles r
    CROSS JOIN LATERAL (
      SELECT (SELECT count(*) FROM pg_class c WHERE c.relowner = r.oid)
           + (SELECT count(*) FROM pg_namespace n WHERE n.nspowner = r.oid)
           + (SELECT count(*) FROM pg_database d WHERE d.datdba = r.oid) AS object_count
    ) owned
    WHERE rolname IN ('trykatch_migrator','trykatch_org_runtime','trykatch_platform_runtime','trykatch_identity_runtime','trykatch_outbox_worker','trykatch_work_coordinator');"
}

printf 'Starting PostgreSQL 18.6 rehearsal source.\n'
start_postgres "$pg18_name" "$postgres18_image"
pg18_port=$(container_port "$pg18_name")
provision_roles_and_database "$pg18_name"
pg18_version=$(server_version "$pg18_port")
[[ "$pg18_version" == 18* ]] || { printf 'Expected PostgreSQL 18, got server_version_num=%s.\n' "$pg18_version" >&2; exit 1; }

run_migrator "$ef10_migrator_dll" "$pg18_port"

psql_container "$pg18_name" postgres trykatch "$pg_admin_password" <<'SQL'
INSERT INTO platform.organizations ("Id", "Name", "Slug", "IsActive", "CreatedAt") VALUES
  ('018f0000-0000-7000-8000-000000000001', 'Upgrade Org Alpha', 'upgrade-alpha', true, '2026-01-01T00:00:00Z'),
  ('018f0000-0000-7000-8000-000000000002', 'Upgrade Org Beta', 'upgrade-beta', true, '2026-01-02T00:00:00Z');
INSERT INTO app.projects ("Id", "OrganizationId", "Name", "Description", "CreatedBy", "CreatedAt") VALUES
  ('018f0000-0000-7000-8000-000000000101', '018f0000-0000-7000-8000-000000000001', 'Alpha preserved project', 'Before cross-major restore', '018f0000-0000-7000-8000-000000000201', '2026-01-03T00:00:00Z'),
  ('018f0000-0000-7000-8000-000000000102', '018f0000-0000-7000-8000-000000000002', 'Beta preserved project', 'Separate organization row', '018f0000-0000-7000-8000-000000000202', '2026-01-04T00:00:00Z');
SQL

baseline_rows=$(stable_row_snapshot "$pg18_port")
baseline_ef_history=$(ef_history_snapshot "$pg18_port")
baseline_module_history=$(module_history_snapshot "$pg18_port")
[[ $(printf '%s\n' "$baseline_rows" | sed -n '1p' | cut -d: -f2) == 2 ]] || { printf 'Expected two seeded organizations.\n' >&2; exit 1; }
[[ $(printf '%s\n' "$baseline_rows" | sed -n '2p' | cut -d: -f2) == 2 ]] || { printf 'Expected two seeded projects.\n' >&2; exit 1; }

run_migrator "$ef11_migrator_dll" "$pg18_port"
pg18_rows=$(stable_row_snapshot "$pg18_port")
pg18_ef_history=$(ef_history_snapshot "$pg18_port")
pg18_module_history=$(module_history_snapshot "$pg18_port")
assert_equal 'PG18 organization/project rows after EF11 upgrade' "$baseline_rows" "$pg18_rows"
missing_ef_history=$(comm -23 <(printf '%s\n' "$baseline_ef_history") <(printf '%s\n' "$pg18_ef_history"))
[[ -z "$missing_ef_history" ]] || { printf 'EF10 migration history missing after upgrade: %s\n' "$missing_ef_history" >&2; exit 1; }
missing_module_history=$(comm -23 <(printf '%s\n' "$baseline_module_history") <(printf '%s\n' "$pg18_module_history"))
[[ -z "$missing_module_history" ]] || { printf 'EF10 module migration history missing after upgrade: %s\n' "$missing_module_history" >&2; exit 1; }

printf 'Dumping PostgreSQL 18 globals and logical database.\n'
docker exec --env PGPASSWORD="$pg_admin_password" "$pg18_name" \
  pg_dumpall --globals-only --username postgres > "$temporary_directory/globals.sql"
sed -E '/^(CREATE ROLE|ALTER ROLE) postgres([[:space:]]|;)/d' \
  "$temporary_directory/globals.sql" > "$temporary_directory/globals-without-bootstrap-role.sql"
docker exec --env PGPASSWORD="$pg_admin_password" "$pg18_name" \
  pg_dump --format=custom --username postgres --dbname trykatch > "$temporary_directory/trykatch.dump"

printf 'Starting PostgreSQL 19.0 rehearsal restore target.\n'
start_postgres "$pg19_name" "$postgres19_image"
docker exec --env PGPASSWORD="$pg_admin_password" --interactive "$pg19_name" \
  psql --no-psqlrc --set ON_ERROR_STOP=1 --username postgres --dbname postgres < "$temporary_directory/globals-without-bootstrap-role.sql"
docker exec --env PGPASSWORD="$pg_admin_password" --interactive "$pg19_name" \
  pg_restore --create --exit-on-error --username postgres --dbname postgres < "$temporary_directory/trykatch.dump"
pg19_port=$(container_port "$pg19_name")
pg19_version=$(server_version "$pg19_port")
[[ "$pg19_version" == 19* ]] || { printf 'Expected PostgreSQL 19, got server_version_num=%s.\n' "$pg19_version" >&2; exit 1; }

assert_equal 'Database owner/runtime role flags/object ownership after restore' \
  "$(role_ownership_snapshot "$pg18_name")" "$(role_ownership_snapshot "$pg19_name")"
assert_equal 'Organization/project rows after PG19 restore' "$pg18_rows" "$(stable_row_snapshot "$pg19_port")"
assert_equal 'EF migration history after PG19 restore' "$pg18_ef_history" "$(ef_history_snapshot "$pg19_port")"
assert_equal 'Module migration history after PG19 restore' "$pg18_module_history" "$(module_history_snapshot "$pg19_port")"

run_migrator "$ef11_migrator_dll" "$pg19_port"
pg19_ef_history=$(ef_history_snapshot "$pg19_port")
pg19_module_history=$(module_history_snapshot "$pg19_port")
first_ef_count=$(ef_history_count "$pg19_port")
first_module_count=$(module_history_count "$pg19_port")
assert_equal 'EF migration history after first PG19 rerun' "$pg18_ef_history" "$pg19_ef_history"
assert_equal 'Module migration history after first PG19 rerun' "$pg18_module_history" "$pg19_module_history"
assert_equal 'Rows after first PG19 rerun' "$pg18_rows" "$(stable_row_snapshot "$pg19_port")"

run_migrator "$ef11_migrator_dll" "$pg19_port"
second_ef_count=$(ef_history_count "$pg19_port")
second_module_count=$(module_history_count "$pg19_port")
assert_equal 'EF migration history after second PG19 rerun' "$pg19_ef_history" "$(ef_history_snapshot "$pg19_port")"
assert_equal 'Module migration history after second PG19 rerun' "$pg19_module_history" "$(module_history_snapshot "$pg19_port")"
assert_equal 'Rows after second PG19 rerun' "$pg18_rows" "$(stable_row_snapshot "$pg19_port")"
assert_equal 'EF history count across PG19 reruns' "$first_ef_count" "$second_ef_count"
assert_equal 'Module history count across PG19 reruns' "$first_module_count" "$second_module_count"

role_state=$(role_ownership_snapshot "$pg19_name")
[[ "$role_state" == *'trykatch_migrator:false:false:false:false:false:false:'* ]] || { printf 'Migrator role does not satisfy restricted login contract: %s\n' "$role_state" >&2; exit 1; }
for role in trykatch_org_runtime trykatch_platform_runtime trykatch_identity_runtime trykatch_outbox_worker trykatch_work_coordinator; do
  [[ "$role_state" == *"$role:false:false:false:false:false:false:0"* ]] || { printf 'Runtime role is unsafe or owns objects: %s\n%s\n' "$role" "$role_state" >&2; exit 1; }
done

isolation_state=$(query_on_port "$pg19_port" postgres "$pg_admin_password" "
  SELECT concat_ws(',',
    (SELECT relrowsecurity FROM pg_class WHERE oid = 'app.projects'::regclass),
    (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'app.projects'::regclass),
    has_table_privilege('trykatch_org_runtime', 'app.projects', 'SELECT'),
    NOT has_table_privilege('trykatch_platform_runtime', 'app.projects', 'SELECT'),
    NOT has_table_privilege('trykatch_identity_runtime', 'app.projects', 'SELECT'),
    has_table_privilege('trykatch_platform_runtime', 'platform.organizations', 'SELECT'),
    has_table_privilege('trykatch_identity_runtime', 'identity.\"AspNetUsers\"', 'SELECT'));")
assert_equal 'Forced RLS and production table grants on PG19' 't,t,t,t,t,t,t' "$isolation_state"

alpha_count=$(query_on_port "$pg19_port" trykatch_org_runtime "$org_runtime_password" \
  "BEGIN; SET LOCAL app.organization_id = '018f0000-0000-7000-8000-000000000001'; SELECT \"Id\" FROM app.projects; COMMIT;")
beta_count=$(query_on_port "$pg19_port" trykatch_org_runtime "$org_runtime_password" \
  "BEGIN; SET LOCAL app.organization_id = '018f0000-0000-7000-8000-000000000002'; SELECT \"Id\" FROM app.projects; COMMIT;")
unscoped_count=$(PGPASSWORD="$org_runtime_password" psql --no-psqlrc --quiet --tuples-only --no-align \
  --set ON_ERROR_STOP=1 --host 127.0.0.1 --port "$pg19_port" --username trykatch_org_runtime --dbname trykatch \
  --command='SELECT count(*) FROM app.projects;')
assert_equal 'Organization alpha runtime visibility' '018f0000-0000-7000-8000-000000000101' "$alpha_count"
assert_equal 'Organization beta runtime visibility' '018f0000-0000-7000-8000-000000000102' "$beta_count"
assert_equal 'Unscoped runtime visibility' '0' "$unscoped_count"

printf 'PostgreSQL upgrade rehearsal passed: EF10 -> EF11, PG%s -> PG%s, retained rows/history, idempotent reruns (EF=%s, module=%s), production ownership/grants, and runtime RLS isolation.\n' \
  "${pg18_version:0:2}" "${pg19_version:0:2}" "$second_ef_count" "$second_module_count"
