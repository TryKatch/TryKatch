#!/usr/bin/env bash
# Sourced only by the isolated generated-application quota qualification mode.
# Every fault and observer belongs to this disposable Compose project.

configure_quota_acceptance() {
  quota_state_file="$test_root/quota-state.json"
  quota_control_dir="$test_root/quota-control"
  quota_ingress_dir="$test_root/quota-ingress"
  quota_policy_file="$test_root/quota-policy.json"
  mkdir -p "$quota_control_dir" "$quota_ingress_dir"
  write_quota_policy 1 true false
  printf '\ninclude /run/quota-ingress/*.conf;\n' >>"$test_root/acceptance-tls.conf"
  cat >"$compose_quota_override" <<EOF
services:
  storage-init:
    image: node:24.7.0-alpine3.22@sha256:be4d5e92ac68483ec71440bf5934865b4b7fcb93588f17a24d411d15f0204e4f
    command: ["chown", "10001:10001", "/data"]
    volumes: ["quota-storage:/data"]
    network_mode: none
    restart: "no"
  object-storage:
    tmpfs: !reset []
    volumes: ["quota-storage:/data"]
    depends_on:
      storage-init:
        condition: service_completed_successfully
  storage-gate:
    image: node:24.7.0-alpine3.22@sha256:be4d5e92ac68483ec71440bf5934865b4b7fcb93588f17a24d411d15f0204e4f
    command: ["node", "/gate.mjs"]
    environment:
      QUOTA_STORAGE_UPSTREAM: http://object-storage:9000
      QUOTA_STORAGE_CONTROL: /control
    volumes:
      - "$repository_root/scripts/lib/quota-storage-gate.mjs:/gate.mjs:ro"
      - "$quota_control_dir:/control"
    depends_on:
      object-storage:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "node", "-e", "fetch('http://127.0.0.1:9000').then(()=>process.exit(0)).catch(()=>process.exit(1))"]
      interval: 2s
      timeout: 3s
      retries: 30
    read_only: true
    security_opt: [no-new-privileges:true]
    cap_drop: [ALL]
  web:
    ports: !override
      - "127.0.0.1:8443:8443"
      - "127.0.0.1:8444:8444"
      - "127.0.0.1:8445:8445"
    volumes:
      - "$quota_ingress_dir:/run/quota-ingress:ro"
  api:
    environment:
      Storage__ServiceUrl: http://storage-gate:9000
    volumes:
      - "$quota_policy_file:/app/appsettings.Production.json:ro"
    depends_on:
      storage-gate:
        condition: service_healthy
volumes:
  quota-storage:
EOF
}

write_quota_policy() {
  local revision=$1 installed=$2 throttle=$3
  # A complete test-owned environment file avoids inherited resource entries
  # after removal. These are explicit qualification allowances, not defaults.
  node --input-type=module - "$quota_policy_file" "$revision" "$installed" "$throttle" <<'NODE'
import { writeFileSync } from 'node:fs'
const [file, revision, installed, throttle] = process.argv.slice(2)
writeFileSync(file, JSON.stringify({
  Quotas: { EnforcementEnabled: true, Revision: Number(revision),
    Resources: installed === 'true' ? { 'documents.storage-bytes': { Limit: 64 } } : {} },
  OrganizationRequestLimits: { Enabled: throttle === 'true', PermitLimit: 1, WindowSeconds: 3600 },
}))
NODE
}

quota_sql() {
  compose exec -T postgres psql --username postgres --dbname trykatch \
    --no-align --tuples-only --set ON_ERROR_STOP=1 "$@"
}

quota_state() {
  node --input-type=module - "$quota_state_file" "$1" <<'NODE'
import { readFileSync } from 'node:fs'
let value = JSON.parse(readFileSync(process.argv[2], 'utf8'))
for (const key of process.argv[3].split('.')) value = value[key]
if (typeof value !== 'string' || !/^[a-f0-9-]{36}$/i.test(value)) throw Error('Expected a public UUID in quota state.')
console.log(value)
NODE
}

run_quota_browser_stage() {
  local stage=$1
  TRYKATCH_ACCEPTANCE_BASE_URL="$web_url" \
  TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
  TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
  TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
  TRYKATCH_ACCEPTANCE_QUOTAS=true \
  TRYKATCH_ACCEPTANCE_QUOTA_STAGE="$stage" \
  TRYKATCH_ACCEPTANCE_QUOTA_STATE="$quota_state_file" \
  TRYKATCH_ACCEPTANCE_QUOTA_CONTROL_DIR="$quota_control_dir" \
  TRYKATCH_ACCEPTANCE_QUOTA_REPLICA_A_URL=https://127.0.0.1:8444 \
  TRYKATCH_ACCEPTANCE_QUOTA_REPLICA_B_URL=https://127.0.0.1:8445 \
  TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results/quotas-$stage" \
  pnpm --dir "$generated_root/web/apps/web" exec playwright test e2e/quotas.spec.ts --grep "quota $stage:" --reporter=line
}

wait_for_quota_marker() {
  local marker=$1 attempts=${2:-300} attempt
  for ((attempt=1; attempt<=attempts; attempt++)); do
    [[ -f "$quota_control_dir/$marker" ]] && return 0
    kill -0 "$quota_browser_pid" 2>/dev/null || {
      printf 'Quota browser exited before marker %s.\n' "$marker" >&2; return 1;
    }
    sleep 1
  done
  printf 'Quota browser did not reach marker %s.\n' "$marker" >&2
  return 1
}

pair_quota_replicas() {
  local ids id address port=8444 index=0
  ids=$(compose ps --status running -q api)
  [[ $(printf '%s\n' "$ids" | wc -l | tr -d ' ') == 2 ]] || {
    printf 'Quota acceptance requires two actual API containers.\n' >&2; return 1;
  }
  quota_replica_a=
  quota_replica_b=
  for id in $ids; do
    address=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$id")
    [[ $address =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
    if [[ $index == 0 ]]; then quota_replica_a=$id; else quota_replica_b=$id; fi
    cat >"$quota_ingress_dir/replica-$index.conf" <<EOF
server {
  listen $port ssl;
  server_name _;
  ssl_certificate /run/acceptance-tls/ingress.crt;
  ssl_certificate_key /run/acceptance-tls/ingress.key;
  ssl_protocols TLSv1.2 TLSv1.3;
  access_log /dev/stdout trykatch_safe;
  error_log /dev/null;
  location / {
    proxy_pass http://$address:8080;
    proxy_http_version 1.1;
    proxy_set_header Host \$http_host;
    proxy_set_header X-Forwarded-For \$remote_addr;
    proxy_set_header X-Forwarded-Proto https;
  }
}
EOF
    index=$((index+1)); port=$((port+1))
  done
  [[ $quota_replica_a != "$quota_replica_b" ]]
  printf '%s\n%s\n' "$quota_replica_a" "$quota_replica_b" >>"$artifact_root/quota-api-processes.txt"
  compose exec -T web nginx -t
  compose exec -T web nginx -s reload
  assert_current_service_images api web migrator
  for port in 8444 8445; do
    local ready=false attempt
    for ((attempt=1; attempt<=60; attempt++)); do
      if curl --fail --insecure --silent "https://127.0.0.1:$port/api/v1/auth/antiforgery" >/dev/null; then
        ready=true; break
      fi
      sleep 1
    done
    [[ $ready == true ]] || { printf 'Paired API %s unavailable.\n' "$port" >&2; return 1; }
  done
}

restart_quota_replicas() {
  local old_a=$quota_replica_a old_b=$quota_replica_b
  compose up --detach --force-recreate --wait --wait-timeout 120 --scale api=2 api
  pair_quota_replicas
  [[ $quota_replica_a != "$old_a" && $quota_replica_a != "$old_b" \
    && $quota_replica_b != "$old_a" && $quota_replica_b != "$old_b" ]]
}

assert_quota_effects() {
  local organization=$1 expected_docs=$2 expected_usage=$3 expected_committed=$4 expected_cancelled=$5 label=$6
  local observed
  observed=$(quota_sql --set organization="$organization" <<'SQL'
SELECT jsonb_build_object(
 'documents', (SELECT count(*) FROM app.documents WHERE "OrganizationId"=:'organization'::uuid),
 'usage', (SELECT COALESCE(sum("Units"),0) FROM app.organization_usage WHERE "OrganizationId"=:'organization'::uuid),
 'committed', (SELECT count(*) FROM app.module_upload_operations WHERE "OrganizationId"=:'organization'::uuid AND "State"=1),
 'cancelled', (SELECT count(*) FROM app.module_upload_operations WHERE "OrganizationId"=:'organization'::uuid AND "State"=3),
 'reserved', (SELECT count(*) FROM app.organization_quota_reservations WHERE "OrganizationId"=:'organization'::uuid AND "State"=0),
 'uploads_audited', (SELECT count(*) FROM platform.audit_entries WHERE "OrganizationId"=:'organization'::uuid AND "Action"='document.uploaded'),
 'uploads_outboxed', (SELECT count(*) FROM platform.outbox_messages WHERE "Payload"::jsonb->>'organizationId'=:'organization' AND "Payload"::jsonb->>'operation'='uploaded'));
SQL
)
  printf '%s\n' "$observed" >"$artifact_root/quota-$label-effects.json"
  node --input-type=module - "$observed" "$expected_docs" "$expected_usage" "$expected_committed" "$expected_cancelled" <<'NODE'
import assert from 'node:assert/strict'
const [value, documents, usage, committed, cancelled] = process.argv.slice(2)
assert.deepEqual(JSON.parse(value), { documents:Number(documents), usage:Number(usage), committed:Number(committed),
  cancelled:Number(cancelled), reserved:0, uploads_audited:Number(committed), uploads_outboxed:Number(committed) })
NODE
}

assert_quota_object() {
  local organization=$1 expected_state=$2 expected_length=$3 label=$4 key
  key=$(quota_sql --set organization="$organization" <<'SQL'
SELECT "ObjectKey" FROM app.module_upload_operations WHERE "OrganizationId"=:'organization'::uuid ORDER BY "CreatedAtUtc" LIMIT 1;
SQL
)
  [[ -n $key ]]
  # Object identity and credentials remain in this private process, never in logs/artifacts.
  compose exec -T object-storage sh -s -- "$key" >"$test_root/object-headers" <<'SH'
set -eu
curl --fail --silent --show-error --head --aws-sigv4 'aws:amz:us-east-1:s3' \
  --user "$RUSTFS_ACCESS_KEY:$RUSTFS_SECRET_KEY" "http://object-storage:9000/trykatch-documents/$1"
SH
  node --input-type=module - "$test_root/object-headers" "$expected_state" "$expected_length" "$artifact_root/quota-$label-storage.json" <<'NODE'
import assert from 'node:assert/strict'
import { readFileSync, writeFileSync } from 'node:fs'
const [file, state, length, output] = process.argv.slice(2)
const headers = Object.fromEntries(readFileSync(file,'utf8').split(/\r?\n/).filter(line=>line.includes(':'))
  .map(line=>[line.slice(0,line.indexOf(':')).toLowerCase(),line.slice(line.indexOf(':')+1).trim()]))
assert.equal(headers['x-amz-meta-tk-upload-state'], state)
assert.equal(headers['content-length'], length)
assert.equal(headers['x-amz-meta-tk-original-length'], '64')
writeFileSync(output, JSON.stringify({ state, length, originalLength:'64' })+'\n')
NODE
  rm "$test_root/object-headers"
}

quota_retention_snapshot() {
  quota_sql --set organization="$1" <<'SQL'
SELECT jsonb_build_object(
 'documents', (SELECT md5(COALESCE(string_agg(to_jsonb(d)::text,'' ORDER BY "Id"),'')) FROM app.documents d WHERE "OrganizationId"=:'organization'::uuid),
 'usage', (SELECT md5(COALESCE(string_agg(to_jsonb(u)::text,'' ORDER BY "EffectId"),'')) FROM app.organization_usage u WHERE "OrganizationId"=:'organization'::uuid),
 'uploads', (SELECT md5(COALESCE(string_agg(to_jsonb(u)::text,'' ORDER BY "OperationId"),'')) FROM app.module_upload_operations u WHERE "OrganizationId"=:'organization'::uuid),
 'history', (SELECT COALESCE(jsonb_agg(jsonb_build_object('revision',"SourceRevision",'digest',md5(to_jsonb(h)::text)) ORDER BY "SourceRevision"),'[]'::jsonb)
   FROM platform.entitlement_publication_history h WHERE "OrganizationId"=:'organization'::uuid));
SQL
}

assert_quota_retention() {
  node --input-type=module - "$artifact_root/quota-retention-before.json" "$1" <<'NODE'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
const [before, after] = process.argv.slice(2).map(file=>JSON.parse(readFileSync(file,'utf8')))
for (const key of ['documents','usage','uploads']) assert.equal(after[key],before[key],`${key} changed across module lifecycle`)
assert.ok(before.history.length>0,'Expected an actual publication history baseline')
for (const row of before.history) assert.deepEqual(after.history.find(item=>item.revision===row.revision),row,'Existing publication history was lost or changed')
NODE
}

run_quota_acceptance() {
  compose up --detach --wait --wait-timeout 120 --scale api=2 api
  pair_quota_replicas
  run_quota_browser_stage setup
  run_quota_browser_stage race
  local race failure expired
  race=$(quota_state organizations.race)
  failure=$(quota_state organizations.failure)
  expired=$(quota_state organizations.expired)
  assert_quota_effects "$race" 1 64 1 0 race

  # The trigger argument is fixed at creation; it never relies on a caller GUC.
  quota_sql --set organization="$failure" <<'SQL'
CREATE OR REPLACE FUNCTION platform.reject_quota_acceptance_audit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."Action"='document.uploaded' AND NEW."OrganizationId"=TG_ARGV[0]::uuid THEN
   RAISE EXCEPTION 'Injected quota acceptance audit failure';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER reject_quota_acceptance_audit BEFORE INSERT ON platform.audit_entries
 FOR EACH ROW EXECUTE FUNCTION platform.reject_quota_acceptance_audit(:'organization');
SQL
  run_quota_browser_stage failed
  assert_quota_effects "$failure" 0 0 0 1 failed
  assert_quota_object "$failure" cancelled 0 failed
  quota_sql --command 'DROP TRIGGER reject_quota_acceptance_audit ON platform.audit_entries; DROP FUNCTION platform.reject_quota_acceptance_audit();'

  touch "$quota_control_dir/arm"
  run_quota_browser_stage expired >"$artifact_root/quota-expired.log" 2>&1 &
  quota_browser_pid=$!
  wait_for_quota_marker expired-ready
  wait_for_quota_marker put-persisted 90
  docker kill --signal KILL "$quota_replica_a"
  [[ $(docker inspect --format '{{.State.Running}}' "$quota_replica_a") == false ]]
  local held
  held=$(quota_sql --set organization="$expired" <<'SQL'
SELECT jsonb_build_object('state',u."State",'hold',r."State",'units',r."Units",
 'lifetime_seconds',EXTRACT(EPOCH FROM (u."ExpiresAtUtc"-u."CreatedAtUtc"))::int,
 'not_expired',u."ExpiresAtUtc">clock_timestamp())
 FROM app.module_upload_operations u JOIN app.organization_quota_reservations r
 ON r."OrganizationId"=u."OrganizationId" AND r."Id"=u."ReservationId"
 WHERE u."OrganizationId"=:'organization'::uuid;
SQL
)
  printf '%s\n' "$held" >"$artifact_root/quota-killed-producer-hold.json"
  node --input-type=module - "$held" <<'NODE'
import assert from 'node:assert/strict'
assert.deepEqual(JSON.parse(process.argv[2]),{state:0,hold:0,units:64,lifetime_seconds:900,not_expired:true})
NODE
  assert_quota_object "$expired" provisional 64 expired-before-restart
  wait_for_quota_marker expired-observed
  wait "$quota_browser_pid"
  quota_browser_pid=
  touch "$quota_control_dir/release"
  compose restart object-storage
  compose up --detach --wait --wait-timeout 120 --scale api=2 api
  pair_quota_replicas
  assert_quota_object "$expired" provisional 64 expired-after-storage-restart

  run_quota_browser_stage throttle >"$artifact_root/quota-throttle.log" 2>&1 &
  quota_browser_pid=$!
  wait_for_quota_marker throttle-ready
  write_quota_policy 1 true true
  restart_quota_replicas
  touch "$quota_control_dir/throttle-enabled"
  wait_for_quota_marker throttle-restart-ready
  restart_quota_replicas
  touch "$quota_control_dir/throttle-restarted"
  wait "$quota_browser_pid"
  quota_browser_pid=
  write_quota_policy 1 true false
  restart_quota_replicas

  # A real operator revision removes an obsolete configured resource before its module.
  compose stop api
  quota_retention_snapshot "$race" >"$artifact_root/quota-retention-before.json"
  write_quota_policy 2 false false
  "$test_root/tools/trykatch" module disable documents --root "$generated_root"
  compose up --detach --build --force-recreate --wait --wait-timeout 120 --scale api=2 api web migrator
  pair_quota_replicas
  run_quota_browser_stage absent
  assert_quota_effects "$race" 1 64 1 0 absent
  quota_retention_snapshot "$race" >"$artifact_root/quota-retention-absent.json"
  assert_quota_retention "$artifact_root/quota-retention-absent.json"
  compose stop api
  "$test_root/tools/trykatch" module enable documents --root "$generated_root"
  write_quota_policy 3 true false
  compose up --detach --build --force-recreate --wait --wait-timeout 120 --scale api=2 api web migrator
  pair_quota_replicas
  # Capture full retained rows before the browser's authorized restore/download/
  # rearchive flow changes lifecycle timestamps while checking the stored bytes.
  quota_retention_snapshot "$race" >"$artifact_root/quota-retention-restored.json"
  assert_quota_retention "$artifact_root/quota-retention-restored.json"
  run_quota_browser_stage restored
  assert_quota_effects "$race" 1 64 1 0 restored

  # The immutable deadline is observed, never rewritten or shortened for this test.
  local remaining attempt ready=false
  for ((attempt=1; attempt<=1100; attempt++)); do
    remaining=$(quota_sql --set organization="$expired" <<'SQL'
SELECT count(*) FROM app.module_upload_operations WHERE "OrganizationId"=:'organization'::uuid AND "State"<>3;
SQL
)
    if [[ $remaining == 0 ]]; then ready=true; break; fi
    sleep 1
  done
  [[ $ready == true ]] || { printf 'Expired upload did not reach confirmed cancellation.\n' >&2; return 1; }
  assert_quota_effects "$expired" 0 0 0 1 expired-recovered
  assert_quota_object "$expired" cancelled 0 expired-recovered
  run_quota_browser_stage recovered
  assert_quota_effects "$expired" 1 64 1 1 recovered-fresh-upload
  printf 'Generated quota replica, real storage/crash/restart and retained Usage UI acceptance passed for %s.\n' "$generated_name"
}
