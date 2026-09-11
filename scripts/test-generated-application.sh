#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-generated-application.XXXXXX")
test_root=$(cd "$test_root" && pwd -P)
generated_root="$test_root/generated"
template_hive="$test_root/template-hive"
environment_file="$test_root/acceptance.env"
compose_override="$test_root/compose.acceptance.yml"
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
compose_started=false

compose() {
  docker compose \
    --project-name "$compose_project" \
    --env-file "$environment_file" \
    --file "$generated_root/compose.yml" \
    --file "$compose_override" \
    "$@"
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

for command in curl docker dotnet openssl pnpm; do
  require_command "$command"
done

network_prefix=$(select_free_network_prefix)

platform_admin_password=${platform_admin_password:-"A1!$(openssl rand -hex 24)"}
platform_manager_password=${platform_manager_password:-"A1!$(openssl rand -hex 24)"}
organization_admin_password=${organization_admin_password:-"A1!$(openssl rand -hex 24)"}
postgres_admin_password="$(openssl rand -hex 24)"
migrator_password="$(openssl rand -hex 24)"
runtime_password="$(openssl rand -hex 24)"
signing_certificate_password="$(openssl rand -hex 24)"
encryption_certificate_password="$(openssl rand -hex 24)"

mkdir -p "$artifact_parent" "$test_root/package"
artifact_root=$(mktemp -d "$artifact_parent/run.XXXXXX")
printf 'project=%s\nbase_url=%s\n' "$generated_name" "$web_url" >"$artifact_root/run.txt"

dotnet pack "$repository_root/Trykatch.Templates.csproj" \
  --configuration Release \
  --output "$test_root/package"
package_path=$(find "$test_root/package" -name 'Trykatch.Templates.*.nupkg' -print -quit)
if [[ -z $package_path ]]; then
  printf 'The Trykatch template package was not produced.\n' >&2
  exit 1
fi

dotnet new --debug:custom-hive "$template_hive" install "$package_path" --force
dotnet new \
  --debug:custom-hive "$template_hive" \
  trykatch \
  --name "$generated_name" \
  --output "$generated_root" \
  --allow-scripts yes

test -f "$generated_root/$generated_namespace.slnx"
test -f "$generated_root/web/package.json"
test -f "$generated_root/compose.yml"
test -d "$generated_root/.git"
test "$(git -C "$generated_root" branch --show-current)" = main

mkdir -p "$generated_root/secrets"
create_certificate signing "$signing_certificate_password"
create_certificate encryption "$encryption_certificate_password"
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
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto https;
  }
}
EOF

cat >"$compose_override" <<EOF
services:
  web:
    environment:
      TRYKATCH_INGRESS_PROXY_IP: 127.0.0.1
    ports: !override
      - "127.0.0.1:8443:8443"
    volumes:
      - "$test_root/acceptance-tls.conf:/etc/nginx/conf.d/acceptance-tls.conf:ro"
      - "$test_root/ingress.crt:/run/acceptance-tls/ingress.crt:ro"
      - "$test_root/ingress.key:/run/acceptance-tls/ingress.key:ro"
EOF

cat >"$environment_file" <<EOF
TRYKATCH_POSTGRES_ADMIN_PASSWORD=$postgres_admin_password
TRYKATCH_MIGRATOR_PASSWORD=$migrator_password
TRYKATCH_ORG_RUNTIME_PASSWORD=$runtime_password-org
TRYKATCH_PLATFORM_RUNTIME_PASSWORD=$runtime_password-platform
TRYKATCH_IDENTITY_RUNTIME_PASSWORD=$runtime_password-identity
TRYKATCH_OUTBOX_WORKER_PASSWORD=$runtime_password-outbox
TRYKATCH_MIGRATOR_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_migrator;Password=$migrator_password
TRYKATCH_ORG_RUNTIME_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_org_runtime;Password=$runtime_password-org
TRYKATCH_PLATFORM_RUNTIME_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_platform_runtime;Password=$runtime_password-platform
TRYKATCH_IDENTITY_RUNTIME_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_identity_runtime;Password=$runtime_password-identity
TRYKATCH_OUTBOX_WORKER_CONNECTION=Host=postgres;Port=5432;Database=trykatch;Username=trykatch_outbox_worker;Password=$runtime_password-outbox
TRYKATCH_PUBLIC_URL=$web_url
TRYKATCH_NETWORK_SUBNET=$network_prefix.0/24
TRYKATCH_NETWORK_DYNAMIC_RANGE=$network_prefix.128/25
TRYKATCH_NETWORK_GATEWAY=$network_prefix.1
TRYKATCH_INGRESS_PROXY_IP=$network_prefix.2
TRYKATCH_WEB_PROXY_IP=$network_prefix.10
TRYKATCH_SECRETS_PATH=$generated_root/secrets
TRYKATCH_SIGNING_CERTIFICATE_PASSWORD=$signing_certificate_password
TRYKATCH_ENCRYPTION_CERTIFICATE_PASSWORD=$encryption_certificate_password
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

compose config --quiet
compose_started=true
compose up --detach --build postgres migrator api web
wait_for_web

pnpm --dir "$generated_root/web" install --frozen-lockfile
TRYKATCH_ACCEPTANCE_BASE_URL="$web_url" \
TRYKATCH_ACCEPTANCE_ADMIN_EMAIL="$platform_admin_email" \
TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD="$platform_admin_password" \
TRYKATCH_ACCEPTANCE_PLATFORM_MANAGER_PASSWORD="$platform_manager_password" \
TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD="$organization_admin_password" \
TRYKATCH_PLAYWRIGHT_OUTPUT_DIR="$generated_root/web/apps/web/test-results" \
pnpm --dir "$generated_root/web/apps/web" exec playwright test \
  e2e/generated-production.spec.ts \
  --reporter=line

printf 'Generated production application acceptance passed for %s.\n' "$generated_name"
