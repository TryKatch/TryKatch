#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-generated-application.XXXXXX")
test_root=$(cd "$test_root" && pwd -P)
generated_root="$test_root/generated"
template_hive="$test_root/template-hive"
environment_file="$test_root/acceptance.env"
compose_override="$test_root/compose.acceptance.yml"
compose_email_override="$test_root/compose.email.yml"
compose_realtime_origin_override="$test_root/compose.realtime-origin.yml"
email_enabled=${TRYKATCH_ACCEPTANCE_EMAIL:-false}
realtime_enabled=${TRYKATCH_ACCEPTANCE_REALTIME:-false}
features_enabled=${TRYKATCH_ACCEPTANCE_FEATURES:-false}
email_state_file="$test_root/email-state.json"
realtime_state_file="$test_root/realtime-state.json"
feature_state_file="$test_root/feature-state.json"
feature_control_dir="$test_root/feature-control"
artifact_parent=${TRYKATCH_ACCEPTANCE_ARTIFACTS:-$repository_root/artifacts/generated-application}
artifact_root=
generated_name=${TRYKATCH_ACCEPTANCE_PROJECT_NAME:-Acme.Acceptance-Portal}
generated_namespace=${generated_name//-/.}
generated_namespace=${generated_namespace// /.}
workspace_suffix=${test_root##*.}
compose_project="trykatch-acceptance-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}-${workspace_suffix}"
compose_project=$(printf '%s' "$compose_project" | tr '[:upper:]_' '[:lower:]-' | cut -c1-63)
web_url=${TRYKATCH_ACCEPTANCE_BASE_URL:-https://127.0.0.1:8443}
platform_admin_email=${TRYKATCH_ACCEPTANCE_ADMIN_EMAIL:-platform.admin@example.test}
platform_admin_password=${TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD:-}
platform_manager_password=${TRYKATCH_ACCEPTANCE_PLATFORM_MANAGER_PASSWORD:-}
organization_admin_password=${TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD:-}
email_operator_password=${TRYKATCH_ACCEPTANCE_EMAIL_OPERATOR_PASSWORD:-}
compose_started=false
vite_pid=
feature_browser_pid=

compose() {
  local files=(--file "$generated_root/compose.yml")
  if [[ $realtime_enabled == true ]]; then
    files+=(--file "$generated_root/compose.realtime.yml")
  fi
  files+=(--file "$compose_override")
  if [[ $email_enabled == true ]]; then
    files+=(--file "$compose_email_override")
  fi
  if [[ -f $compose_realtime_origin_override ]]; then
    files+=(--file "$compose_realtime_origin_override")
  fi
  docker compose \
    --project-name "$compose_project" \
    --env-file "$environment_file" \
    "${files[@]}" \
    "$@"
}

assert_current_service_images() {
  local service container_id image_reference actual_image expected_image containers
  for service in "$@"; do
    containers=$(compose ps --all --quiet "$service")
    [[ -n $containers ]] || { printf 'No container exists for rebuilt service %s.\n' "$service" >&2; return 1; }
    for container_id in $containers; do
      image_reference=$(docker inspect --format '{{.Config.Image}}' "$container_id")
      actual_image=$(docker inspect --format '{{.Image}}' "$container_id")
      expected_image=$(docker image inspect --format '{{.Id}}' "$image_reference")
      [[ $actual_image == "$expected_image" ]] || {
        printf 'Service %s retained a container from an older image.\n' "$service" >&2
        return 1
      }
    done
  done
}

collect_artifacts() {
  if [[ -z $artifact_root ]]; then
    return
  fi
  mkdir -p "$artifact_root"
  if [[ $compose_started == true ]]; then
    compose ps --all >"$artifact_root/compose-ps.txt" 2>&1 || true
    compose logs --no-color >"$artifact_root/compose.log" 2>&1 || true
  fi
  if [[ -d "$generated_root/web/apps/web/test-results" ]]; then
    cp -R "$generated_root/web/apps/web/test-results" "$artifact_root/playwright-test-results"
  fi
}

cleanup() {
  local exit_code=$?
  if [[ -n $feature_browser_pid ]]; then
    kill "$feature_browser_pid" >/dev/null 2>&1 || true
    wait "$feature_browser_pid" >/dev/null 2>&1 || true
  fi
  if [[ -n $vite_pid ]]; then
    kill "$vite_pid" >/dev/null 2>&1 || true
    wait "$vite_pid" >/dev/null 2>&1 || true
  fi
  collect_artifacts
  if [[ $compose_started == true ]]; then
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  if [[ ${TRYKATCH_ACCEPTANCE_KEEP_WORKSPACE:-false} == true ]]; then
    printf 'Generated acceptance workspace retained at %s\n' "$test_root"
  else
    rm -rf "$test_root"
  fi
  exit "$exit_code"
}
trap cleanup EXIT

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    printf 'Required command is unavailable: %s\n' "$1" >&2
    exit 2
  fi
}

select_free_network_prefix() {
  local probe_name="${compose_project}-network-probe"
  local prefix

  for prefix in \
    172.30.240 172.30.241 172.30.242 172.30.243 172.30.244 \
    172.31.240 172.31.241 172.31.242 172.31.243 172.31.244 \
    10.240.240 10.240.241 10.240.242 10.240.243 10.240.244; do
    if docker network create --subnet "$prefix.0/24" "$probe_name" >/dev/null 2>&1; then
      docker network rm "$probe_name" >/dev/null
      printf '%s' "$prefix"
      return 0
    fi
  done

  printf 'Unable to reserve a non-overlapping Docker subnet for generated-application acceptance.\n' >&2
  return 1
}

wait_for_web() {
  local attempts=${TRYKATCH_ACCEPTANCE_HEALTH_ATTEMPTS:-60}
  local delay=${TRYKATCH_ACCEPTANCE_HEALTH_DELAY_SECONDS:-2}
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if curl --fail --insecure --silent --show-error "$web_url/healthz" >/dev/null 2>&1; then
      return 0
    fi
    sleep "$delay"
  done
  printf 'Generated application did not become healthy at %s within %s attempts.\n' "$web_url" "$attempts" >&2
  return 1
}

wait_for_api() {
  for ((attempt = 1; attempt <= 60; attempt++)); do
    if curl --fail --insecure --silent "$web_url/api/v1/auth/antiforgery" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  printf 'The generated API did not become reachable through the web proxy.\n' >&2
  return 1
}

run_feature_browser_stage() {
  local stage=$1 selection
  case "$stage" in
    controls) selection='controls, live UI enforcement' ;;
    absent) selection='an absent module' ;;
    restored) selection='restored modules retain' ;;
    *) printf 'Unknown feature browser stage: %s.\n' "$stage" >&2; return 2 ;;
  esac
  TRYKATCH_ACCEPTANCE_BASE_URL="$web_url" \
  TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
  TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
  TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
  TRYKATCH_ACCEPTANCE_FEATURES=true \
  TRYKATCH_ACCEPTANCE_FEATURE_STAGE="$stage" \
  TRYKATCH_ACCEPTANCE_FEATURE_STATE="$feature_state_file" \
  TRYKATCH_ACCEPTANCE_FEATURE_CONTROL_DIR="$feature_control_dir" \
  TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results/features-$stage" \
  pnpm --dir "$generated_root/web/apps/web" exec playwright test e2e/features.spec.ts --grep "$selection" --reporter=line
}

wait_for_feature_marker() {
  local marker=$1 attempts=${2:-120} attempt
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    [[ -f $feature_control_dir/$marker ]] && return 0
    kill -0 "$feature_browser_pid" 2>/dev/null || {
      printf 'Feature browser exited before marker %s; see feature-controls.log.\n' "$marker" >&2
      return 1
    }
    sleep 1
  done
  printf 'Feature browser did not reach marker %s.\n' "$marker" >&2
  return 1
}

wait_for_project_effects() {
  local pending attempt
  for ((attempt = 1; attempt <= 60; attempt++)); do
    pending=$(compose exec -T postgres psql --username postgres --dbname trykatch --no-align --tuples-only --set ON_ERROR_STOP=1 <<'SQL'
SELECT
  (SELECT count(*) FROM platform.outbox_messages
   WHERE ("Type" LIKE '%.ProjectChanged' OR "Type" = 'projects.created') AND "ProcessedAt" IS NULL)
  + (SELECT count(*) FROM infrastructure.module_event_deliveries
     WHERE "EventKey" LIKE 'projects.%' AND "State" <> 'completed')
  + (SELECT count(*) FROM platform.audit_intents i
     WHERE i."Operation" LIKE 'project.%' AND NOT EXISTS (SELECT 1 FROM platform.audit_entries a WHERE a."Id" = i."Id"));
SQL
)
    [[ $pending == 0 ]] && return 0
    sleep 1
  done
  printf 'Projects effects did not settle before the retention boundary.\n' >&2
  return 1
}

feature_business_counts() {
  compose exec -T postgres psql --username postgres --dbname trykatch --no-align --tuples-only --set ON_ERROR_STOP=1 <<'SQL'
SELECT jsonb_build_object(
  'projects', (SELECT count(*) FROM app.projects),
  'audit_intents', (SELECT count(*) FROM platform.audit_intents WHERE "Operation" LIKE 'project.%'),
  'outbox', (SELECT count(*) FROM platform.outbox_messages),
  'receipts', (SELECT count(*) FROM app.mutation_receipts));
SQL
}

feature_retention_snapshot() {
  # API containers are stopped at each snapshot, so worker activity cannot race the comparison.
  # Hash full control/history rows as well as counting them: equal counts alone do not prove retention.
  compose exec -T postgres psql --username postgres --dbname trykatch --no-align --tuples-only --set ON_ERROR_STOP=1 <<'SQL'
SELECT jsonb_build_object(
  'projects', (SELECT count(*) FROM app.projects),
  'project_data', (SELECT md5(COALESCE(string_agg(to_jsonb(p)::text, '' ORDER BY "Id"), '')) FROM app.projects p),
  'audit_intents', (SELECT count(*) FROM platform.audit_intents WHERE "Operation" LIKE 'project.%'),
  'outbox', (SELECT count(*) FROM platform.outbox_messages),
  'receipts', (SELECT count(*) FROM app.mutation_receipts),
  'realtime_history', (SELECT count(*) FROM app.realtime_history WHERE "ModuleId" = 'projects'),
  'environment_controls', (SELECT count(*) FROM platform.feature_environment_controls),
  'organization_controls', (SELECT count(*) FROM platform.organization_feature_overrides),
  'feature_history', (SELECT count(*) FROM platform.feature_control_history),
  'environment_data', (SELECT md5(COALESCE(string_agg(to_jsonb(e)::text, '' ORDER BY "Environment", "FeatureKey"), '')) FROM platform.feature_environment_controls e),
  'organization_data', (SELECT md5(COALESCE(string_agg(to_jsonb(o)::text, '' ORDER BY "OrganizationId", "Environment", "FeatureKey"), '')) FROM platform.organization_feature_overrides o),
  'history_data', (SELECT md5(COALESCE(string_agg(to_jsonb(h)::text, '' ORDER BY "Id"), '')) FROM platform.feature_control_history h));
SQL
}

create_certificate() {
  local name=$1
  local password=$2
  openssl req \
    -x509 \
    -newkey rsa:3072 \
    -sha256 \
    -nodes \
    -days 2 \
    -subj "/CN=Trykatch acceptance $name" \
    -keyout "$test_root/$name.key" \
    -out "$test_root/$name.crt" \
    >/dev/null 2>&1
  openssl pkcs12 \
    -export \
    -out "$generated_root/secrets/$name.pfx" \
    -inkey "$test_root/$name.key" \
    -in "$test_root/$name.crt" \
    -passout "pass:$password" \
    >/dev/null 2>&1
  # The production image runs as the unprivileged .NET app user. The certificate
  # is an ephemeral CI asset in a private temporary directory and is mounted
  # read-only, so it must be readable by that container user.
  chmod 0644 "$generated_root/secrets/$name.pfx"
  printf '%s' "$password" >"$generated_root/secrets/$name.password"
  chmod 0644 "$generated_root/secrets/$name.password"
}

create_ingress_certificate() {
  openssl req \
    -x509 \
    -newkey rsa:3072 \
    -sha256 \
    -nodes \
    -days 2 \
    -subj "/CN=127.0.0.1" \
    -addext "subjectAltName=IP:127.0.0.1,DNS:localhost" \
    -keyout "$test_root/ingress.key" \
    -out "$test_root/ingress.crt" \
    >/dev/null 2>&1
  chmod 0644 "$test_root/ingress.key" "$test_root/ingress.crt"
}

create_smtp_certificate() {
  mkdir -p "$test_root/smtp"
  openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 2 \
    -subj "/CN=Acceptance SMTP Root" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -keyout "$test_root/smtp/root.key" -out "$test_root/smtp/root.crt" >/dev/null 2>&1
  openssl req -newkey rsa:3072 -sha256 -nodes -subj "/CN=mailpit" \
    -keyout "$test_root/smtp/server.key" -out "$test_root/smtp/server.csr" >/dev/null 2>&1
  cat >"$test_root/smtp/server.ext" <<'EOF'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:mailpit
crlDistributionPoints=URI:http://web:8080/acceptance-smtp.crl
EOF
  openssl x509 -req -sha256 -days 2 -in "$test_root/smtp/server.csr" \
    -CA "$test_root/smtp/root.crt" -CAkey "$test_root/smtp/root.key" -CAcreateserial \
    -extfile "$test_root/smtp/server.ext" -out "$test_root/smtp/server.crt" >/dev/null 2>&1
  # MailKit's normal validation checks revocation. Publish an empty, signed CRL
  # for this private test CA rather than disabling that production check.
  touch "$test_root/smtp/index.txt"
  printf '01\n' >"$test_root/smtp/crlnumber"
  cat >"$test_root/smtp/ca.cnf" <<EOF
[ca]
default_ca=acceptance_ca
[acceptance_ca]
database=$test_root/smtp/index.txt
private_key=$test_root/smtp/root.key
certificate=$test_root/smtp/root.crt
crlnumber=$test_root/smtp/crlnumber
default_md=sha256
default_crl_days=2
[crl_extensions]
authorityKeyIdentifier=keyid:always
EOF
  openssl ca -gencrl -config "$test_root/smtp/ca.cnf" -crlexts crl_extensions \
    -out "$test_root/smtp/crl.pem" >/dev/null 2>&1
  openssl crl -in "$test_root/smtp/crl.pem" -outform DER -out "$test_root/smtp/smtp.crl"
  # Temporary assets in the private acceptance workspace must be readable by
  # the unprivileged containers. The API receives only the public fixture root.
  chmod 0644 "$test_root/smtp/root.crt" "$test_root/smtp/server.crt" "$test_root/smtp/server.key" "$test_root/smtp/smtp.crl"
}

run_email_browser_stage() {
  local stage=$1
  TRYKATCH_ACCEPTANCE_BASE_URL="$web_url" \
  TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
  TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
  TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
  TRYKATCH_ACCEPTANCE_EMAIL=true \
  TRYKATCH_ACCEPTANCE_EMAIL_STAGE="$stage" \
  TRYKATCH_ACCEPTANCE_EMAIL_STATE="$email_state_file" \
  TRYKATCH_ACCEPTANCE_EMAIL_OPERATOR_PASSWORD="$email_operator_password" \
  TRYKATCH_ACCEPTANCE_INBOX_URL="${inbox_url:-}" \
  TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results/$stage" \
  pnpm --dir "$generated_root/web/apps/web" exec playwright test \
    e2e/generated-email-production.spec.ts --reporter=line
}

for command in curl docker dotnet openssl pnpm node; do
  require_command "$command"
done
case "$email_enabled" in
  true|false) ;;
  *) printf 'TRYKATCH_ACCEPTANCE_EMAIL must be true or false.\n' >&2; exit 2 ;;
esac
case "$realtime_enabled" in
  true|false) ;;
  *) printf 'TRYKATCH_ACCEPTANCE_REALTIME must be true or false.\n' >&2; exit 2 ;;
esac
case "$features_enabled" in
  true|false) ;;
  *) printf 'TRYKATCH_ACCEPTANCE_FEATURES must be true or false.\n' >&2; exit 2 ;;
esac
if [[ $email_enabled == true && $realtime_enabled == true ]] \
  || [[ $features_enabled == true && ( $email_enabled == true || $realtime_enabled == true ) ]]; then
  printf 'Select email, realtime or feature acceptance separately.\n' >&2
  exit 2
fi

network_prefix=$(select_free_network_prefix)

platform_admin_password=${platform_admin_password:-"A1!$(openssl rand -hex 24)"}
platform_manager_password=${platform_manager_password:-"A1!$(openssl rand -hex 24)"}
organization_admin_password=${organization_admin_password:-"A1!$(openssl rand -hex 24)"}
email_operator_password=${email_operator_password:-"A1!$(openssl rand -hex 24)"}
postgres_admin_password="$(openssl rand -hex 24)"
migrator_password="$(openssl rand -hex 24)"
runtime_password="$(openssl rand -hex 24)"
signing_certificate_password="$(openssl rand -hex 24)"
encryption_certificate_password="$(openssl rand -hex 24)"
data_protection_certificate_password="$(openssl rand -hex 24)"
storage_access_key=acceptance-storage
storage_secret_key="$(openssl rand -hex 24)"
realtime_password="$(openssl rand -hex 24)"

mkdir -p "$artifact_parent" "$test_root/package"
artifact_root=$(mktemp -d "$artifact_parent/run.XXXXXX")
printf 'project=%s\nbase_url=%s\n' "$generated_name" "$web_url" >"$artifact_root/run.txt"

source "$repository_root/scripts/lib/qualification-packages.sh"
prepare_qualification_packages "$test_root/package"
package_path=$qualification_template_package
if [[ -z $package_path ]]; then
  printf 'The Trykatch template package was not produced.\n' >&2
  exit 1
fi

dotnet new --debug:custom-hive "$template_hive" install "$package_path" --force
install_qualification_cli "$test_root"
"$test_root/tools/trykatch" new "$generated_name" \
  --debug:custom-hive "$template_hive" \
  --output "$generated_root" \
  --email "$email_enabled"

test -f "$generated_root/$generated_namespace.slnx"
test -f "$generated_root/web/package.json"
test -f "$generated_root/web/packages/api-client/src/generated/client.ts"
test -f "$generated_root/web/packages/api-client/src/generated/models/index.ts"
test -f "$generated_root/compose.yml"
test -d "$generated_root/.git"
test "$(git -C "$generated_root" branch --show-current)" = main

mkdir -p "$generated_root/secrets"
create_certificate signing "$signing_certificate_password"
create_certificate encryption "$encryption_certificate_password"
create_certificate data-protection "$data_protection_certificate_password"
create_ingress_certificate

cat >"$test_root/acceptance-tls.conf" <<'EOF'
server {
  listen 8443 ssl;
  server_name _;

  ssl_certificate /run/acceptance-tls/ingress.crt;
  ssl_certificate_key /run/acceptance-tls/ingress.key;
  ssl_protocols TLSv1.2 TLSv1.3;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $trykatch_connection_upgrade;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto https;
  }
}
EOF

cat >"$compose_override" <<EOF
services:
  object-storage:
    image: docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b
    command: ["/data"]
    environment:
      RUSTFS_ACCESS_KEY: $storage_access_key
      RUSTFS_SECRET_KEY: $storage_secret_key
      RUSTFS_ADDRESS: ":9000"
      RUSTFS_CONSOLE_ENABLE: "false"
    tmpfs: ["/data:mode=755,uid=10001,gid=10001"]
    healthcheck:
      test: ["CMD", "curl", "--fail", "--silent", "http://127.0.0.1:9000/health/ready"]
      interval: 5s
      timeout: 3s
      retries: 12
      start_period: 10s
    security_opt: [no-new-privileges:true]
    cap_drop: [ALL]
  web:
    environment:
      TRYKATCH_INGRESS_PROXY_IP: 127.0.0.1
    ports: !override
      - "127.0.0.1:8443:8443"
    volumes:
      - "$test_root/acceptance-tls.conf:/etc/nginx/conf.d/acceptance-tls.conf:ro"
      - "$test_root/ingress.crt:/run/acceptance-tls/ingress.crt:ro"
      - "$test_root/ingress.key:/run/acceptance-tls/ingress.key:ro"
  api:
    environment:
      Storage__CreateBucket: "true"
    depends_on:
      object-storage:
        condition: service_healthy
EOF

cat >"$environment_file" <<EOF
TRYKATCH_POSTGRES_ADMIN_PASSWORD=$postgres_admin_password
TRYKATCH_MIGRATOR_PASSWORD=$migrator_password
TRYKATCH_ORG_RUNTIME_PASSWORD=$runtime_password-org
TRYKATCH_PLATFORM_RUNTIME_PASSWORD=$runtime_password-platform
TRYKATCH_IDENTITY_RUNTIME_PASSWORD=$runtime_password-identity
TRYKATCH_OUTBOX_WORKER_PASSWORD=$runtime_password-outbox
TRYKATCH_WORK_COORDINATOR_PASSWORD=$runtime_password-work-coordinator
TRYKATCH_MIGRATOR_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_migrator;Password=$migrator_password
TRYKATCH_ORG_RUNTIME_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_org_runtime;Password=$runtime_password-org
TRYKATCH_PLATFORM_RUNTIME_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_platform_runtime;Password=$runtime_password-platform
TRYKATCH_IDENTITY_RUNTIME_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_identity_runtime;Password=$runtime_password-identity
TRYKATCH_OUTBOX_WORKER_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_outbox_worker;Password=$runtime_password-outbox
TRYKATCH_WORK_COORDINATOR_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_work_coordinator;Password=$runtime_password-work-coordinator
TRYKATCH_STORAGE_SERVICE_URL=http://object-storage:9000
TRYKATCH_STORAGE_ACCESS_KEY=$storage_access_key
TRYKATCH_STORAGE_SECRET_KEY=$storage_secret_key
TRYKATCH_STORAGE_BUCKET=trykatch-documents
TRYKATCH_STORAGE_REGION=us-east-1
TRYKATCH_PUBLIC_URL=$web_url
TRYKATCH_REALTIME_NAMESPACE=generated-acceptance
TRYKATCH_REALTIME_PASSWORD=$realtime_password
TRYKATCH_NETWORK_SUBNET=$network_prefix.0/24
TRYKATCH_NETWORK_DYNAMIC_RANGE=$network_prefix.128/25
TRYKATCH_NETWORK_GATEWAY=$network_prefix.1
TRYKATCH_INGRESS_PROXY_IP=$network_prefix.2
TRYKATCH_WEB_PROXY_IP=$network_prefix.10
TRYKATCH_SECRETS_PATH=$generated_root/secrets
TRYKATCH_BOOTSTRAP_ADMIN_EMAIL=$platform_admin_email
TRYKATCH_BOOTSTRAP_ADMIN_PASSWORD=$platform_admin_password
TRYKATCH_ENVIRONMENT=Production
TRYKATCH_RELEASE_VERSION=acceptance
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_SERVICE_NAME=acme-acceptance-portal-api
OTEL_SERVICE_NAMESPACE=acceptance
OTEL_DEPLOYMENT_ENVIRONMENT=ci
OTEL_TRACES_SAMPLER_ARG=1.0
GRAFANA_ADMIN_PASSWORD=$(openssl rand -hex 24)
EOF
chmod 600 "$environment_file"

if [[ $email_enabled == true ]]; then
  create_smtp_certificate
  cat >"$compose_email_override" <<EOF
services:
  web:
    volumes:
      - "$test_root/smtp/smtp.crl:/usr/share/nginx/html/acceptance-smtp.crl:ro"
  api:
    environment:
      Email__Host: mailpit
      Email__Port: "1025"
      Email__From: "Acceptance <noreply@example.invalid>"
      Email__Security: StartTls
      Jobs__Enabled: "false"
      SSL_CERT_FILE: /run/acceptance-smtp/root.crt
    volumes:
      - "$test_root/smtp/root.crt:/run/acceptance-smtp/root.crt:ro"
  mailpit:
    image: docker.io/axllent/mailpit:v1.30.4@sha256:5a49a77c5bdbe7c5474450b4f46348d09949df3695257729c93a30369382d4f6
    environment:
      MP_SMTP_TLS_CERT: /run/acceptance-smtp/server.crt
      MP_SMTP_TLS_KEY: /run/acceptance-smtp/server.key
      MP_SMTP_REQUIRE_STARTTLS: "true"
    volumes:
      - "$test_root/smtp/server.crt:/run/acceptance-smtp/server.crt:ro"
      - "$test_root/smtp/server.key:/run/acceptance-smtp/server.key:ro"
    ports:
      - "127.0.0.1::8025"
    security_opt: [no-new-privileges:true]
    cap_drop: [ALL]
EOF
fi

compose config --quiet
compose_started=true
compose up --detach --build postgres migrator api web
wait_for_web

pnpm --dir "$generated_root/web" install --frozen-lockfile
if [[ $realtime_enabled == true ]]; then
  compose up --detach --wait --wait-timeout 120 --scale api=2 api
  test "$(compose ps --status running -q api | wc -l | tr -d ' ')" = 2
  wait_for_web
  wait_for_api
  TRYKATCH_ACCEPTANCE_BASE_URL="$web_url" \
  TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
  TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
  TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
  TRYKATCH_ACCEPTANCE_REALTIME=true \
  TRYKATCH_ACCEPTANCE_REALTIME_STATE="$realtime_state_file" \
  TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results" \
  pnpm --dir "$generated_root/web/apps/web" exec playwright test e2e/realtime.spec.ts --reporter=line
  cat >"$generated_root/web/apps/web/realtime-proxy-acceptance.mjs" <<'EOF'
import { createServer } from 'vite'
import { readFileSync, writeFileSync } from 'node:fs'
const server = await createServer({
  server: {
    host: '127.0.0.1', port: 0,
    https: { key: readFileSync(process.env.ACCEPTANCE_TLS_KEY), cert: readFileSync(process.env.ACCEPTANCE_TLS_CERT) },
  },
})
await server.listen()
const address = server.httpServer.address()
if (!address || typeof address === 'string') throw new Error('Vite acceptance endpoint unavailable.')
writeFileSync(process.env.ACCEPTANCE_VITE_READY, `https://127.0.0.1:${address.port}`)
process.on('SIGTERM', async () => { await server.close(); process.exit(0) })
EOF
  (
    cd "$generated_root/web/apps/web"
    ACCEPTANCE_TLS_KEY="$test_root/ingress.key" \
    ACCEPTANCE_TLS_CERT="$test_root/ingress.crt" \
    ACCEPTANCE_VITE_READY="$test_root/vite-ready" \
    services__api__https__0="$web_url" \
    node realtime-proxy-acceptance.mjs
  ) >"$artifact_root/realtime-vite.log" 2>&1 &
  vite_pid=$!
  for ((attempt = 1; attempt <= 60; attempt++)); do
    [[ -s $test_root/vite-ready ]] && break
    kill -0 "$vite_pid" 2>/dev/null || { printf 'Vite acceptance process exited before readiness.\n' >&2; exit 1; }
    sleep 1
  done
  [[ -s $test_root/vite-ready ]] || { printf 'Vite acceptance did not become ready.\n' >&2; exit 1; }
  vite_url=$(cat "$test_root/vite-ready")
  cat >"$compose_realtime_origin_override" <<EOF
services:
  api:
    environment:
      Application__PublicUrl: "$vite_url"
EOF
  compose up --detach --wait --wait-timeout 120 --scale api=2 api
  wait_for_api
  TRYKATCH_ACCEPTANCE_BASE_URL="$vite_url" \
  TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
  TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
  TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
  TRYKATCH_ACCEPTANCE_REALTIME=true \
  TRYKATCH_ACCEPTANCE_REALTIME_STATE="$realtime_state_file" \
  TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results/realtime-vite" \
  pnpm --dir "$generated_root/web/apps/web" exec playwright test e2e/realtime.spec.ts --reporter=line
  test -s "$realtime_state_file"
  old_realtime_api_containers=$(compose ps --all --quiet api)
  [[ -n $old_realtime_api_containers ]]
  compose stop api
  test -z "$(compose ps --status running -q api)"
  history_before=$(compose exec -T postgres psql --username postgres --dbname trykatch --no-align --tuples-only \
    --command 'SELECT count(*) FROM app.realtime_history WHERE "ModuleId" = $module$projects$module$;' | tr -d '[:space:]')
  [[ $history_before =~ ^[0-9]+$ && $history_before -gt 0 ]] || { printf 'Realtime module-removal fixture has no committed history.\n' >&2; exit 1; }
  # Documents declares a Projects dependency and contributes to its extension point.
  # Remove dependents first through the normal validated lifecycle.
  "$test_root/tools/trykatch" module disable documents --root "$generated_root"
  "$test_root/tools/trykatch" module disable projects --root "$generated_root"
  # Explicitly deploy new immutable hosts. Some Compose versions restart stopped
  # API containers after a build without replacing their old image.
  compose up --detach --build --force-recreate --wait --wait-timeout 120 --scale api=2 api web migrator
  assert_current_service_images api web migrator
  for container_id in $(compose ps --all --quiet api); do
    for old_container_id in $old_realtime_api_containers; do
      [[ $container_id != "$old_container_id" ]] || {
        printf 'Module removal retained an old API container.\n' >&2
        exit 1
      }
    done
  done
  wait_for_api
  history_after=$(compose exec -T postgres psql --username postgres --dbname trykatch --no-align --tuples-only \
    --command 'SELECT count(*) FROM app.realtime_history WHERE "ModuleId" = $module$projects$module$;' | tr -d '[:space:]')
  [[ $history_after == "$history_before" ]] || { printf 'Module removal changed committed realtime history.\n' >&2; exit 1; }
  TRYKATCH_ACCEPTANCE_BASE_URL="$vite_url" \
  TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
  TRYKATCH_ACCEPTANCE_REALTIME=true \
  TRYKATCH_ACCEPTANCE_REALTIME_STATE="$realtime_state_file" \
  TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results/realtime-module-absence" \
  pnpm --dir "$generated_root/web/apps/web" exec playwright test e2e/realtime-module-absence.spec.ts --reporter=line
  printf 'Generated realtime application acceptance passed for %s.\n' "$generated_name"
  exit 0
fi
if [[ $email_enabled == true ]]; then
  # Deliberately leave the SMTP service stopped while durable intent is admitted.
  run_email_browser_stage prepare
  test -s "$email_state_file"
  compose stop api
  compose up --detach mailpit
  inbox_url="http://$(compose port mailpit 8025)"
  inbox_ready=false
  for ((attempt = 1; attempt <= 30; attempt++)); do
    if curl --fail --silent "$inbox_url/api/v1/messages" >/dev/null; then
      inbox_ready=true
      break
    fi
    sleep 1
  done
  if [[ $inbox_ready != true ]]; then
    printf 'The isolated SMTP inbox did not become ready.\n' >&2
    exit 1
  fi
  compose up --detach --wait --wait-timeout 120 --scale api=2 api
  test "$(compose ps --status running -q api | wc -l | tr -d ' ')" = 2
  wait_for_web
  wait_for_api
  run_email_browser_stage resume
  compose logs --no-color api >"$artifact_root/api-email.log"
  node "$repository_root/scripts/assert-email-log-privacy.mjs" "$email_state_file" "$artifact_root/api-email.log"
  printf 'Generated email application restart/competing-worker acceptance passed for %s.\n' "$generated_name"
  exit 0
fi
TRYKATCH_ACCEPTANCE_BASE_URL="$web_url" \
TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
TRYKATCH_ACCEPTANCE_PLATFORM_MANAGER_PASSWORD="$platform_manager_password" \
TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results" \
pnpm --dir "$generated_root/web/apps/web" exec playwright test \
  e2e/generated-production.spec.ts \
  --reporter=line

if [[ $features_enabled == true ]]; then
  mkdir -p "$feature_control_dir"
  run_feature_browser_stage controls >"$artifact_root/feature-controls.log" 2>&1 &
  feature_browser_pid=$!
  wait_for_feature_marker outage-ready 600
  wait_for_project_effects
  feature_business_counts >"$artifact_root/feature-outage-before.json"
  # A real, isolated policy-store privilege outage keeps authentication and the
  # platform operator available. No production test endpoint or fabricated 503 is used.
  test "$(compose exec -T postgres psql --username postgres --dbname trykatch -Atc \
    "SELECT has_table_privilege('trykatch_org_runtime', 'platform.feature_environment_controls', 'SELECT');")" = t
  compose exec -T postgres psql --username postgres --dbname trykatch --set ON_ERROR_STOP=1 \
    --command 'REVOKE SELECT ON platform.feature_environment_controls FROM trykatch_org_runtime;'
  test "$(compose exec -T postgres psql --username postgres --dbname trykatch -Atc \
    "SELECT has_table_privilege('trykatch_org_runtime', 'platform.feature_environment_controls', 'SELECT');")" = f
  touch "$feature_control_dir/policy-blocked"
  wait_for_feature_marker restore-requested
  feature_business_counts >"$artifact_root/feature-outage-after.json"
  cmp "$artifact_root/feature-outage-before.json" "$artifact_root/feature-outage-after.json"
  compose exec -T postgres psql --username postgres --dbname trykatch --set ON_ERROR_STOP=1 \
    --command 'GRANT SELECT ON platform.feature_environment_controls TO trykatch_org_runtime;'
  test "$(compose exec -T postgres psql --username postgres --dbname trykatch -Atc \
    "SELECT has_table_privilege('trykatch_org_runtime', 'platform.feature_environment_controls', 'SELECT');")" = t
  touch "$feature_control_dir/policy-restored"
  wait "$feature_browser_pid"
  feature_browser_pid=
  test -s "$feature_state_file"
  wait_for_project_effects
  compose stop api
  test -z "$(compose ps --status running -q api)"
  feature_retention_snapshot >"$artifact_root/feature-retention-before.json"
  "$test_root/tools/trykatch" module disable documents --root "$generated_root"
  "$test_root/tools/trykatch" module disable projects --root "$generated_root"
  compose up --detach --build --force-recreate --wait --wait-timeout 120 api web migrator
  assert_current_service_images api web migrator
  wait_for_api
  run_feature_browser_stage absent
  compose stop api
  feature_retention_snapshot >"$artifact_root/feature-retention-absent.json"
  cmp "$artifact_root/feature-retention-before.json" "$artifact_root/feature-retention-absent.json"
  "$test_root/tools/trykatch" module enable projects --root "$generated_root"
  "$test_root/tools/trykatch" module enable documents --root "$generated_root"
  compose up --detach --build --force-recreate --wait --wait-timeout 120 api web migrator
  assert_current_service_images api web migrator
  wait_for_api
  run_feature_browser_stage restored
  compose stop api
  feature_retention_snapshot >"$artifact_root/feature-retention-restored.json"
  cmp "$artifact_root/feature-retention-before.json" "$artifact_root/feature-retention-restored.json"
  printf 'Generated feature controls, real policy outage/recovery and retained module lifecycle acceptance passed for %s.\n' "$generated_name"
fi

printf 'Generated production application acceptance passed for %s.\n' "$generated_name"
