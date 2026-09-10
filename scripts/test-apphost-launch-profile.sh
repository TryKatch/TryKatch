#!/usr/bin/env bash
set -euo pipefail

application_root=${1:?"usage: test-apphost-launch-profile.sh <generated-application-root>"}
apphost_project=$(find "$application_root/src" -type f -name '*.AppHost.csproj' -print -quit)

if [[ -z "$apphost_project" ]]; then
  printf 'AppHost launch-profile contract failed: no AppHost project found under %s/src\n' "$application_root" >&2
  exit 1
fi

launch_settings="$(dirname "$apphost_project")/Properties/launchSettings.json"
apphost_program="$(dirname "$apphost_project")/Program.cs"
if [[ ! -f "$launch_settings" ]]; then
  printf 'AppHost launch-profile contract failed: %s is missing\n' "$launch_settings" >&2
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  printf 'AppHost launch-profile contract failed: jq is required to validate launchSettings.json\n' >&2
  exit 1
fi

if ! jq -e '
  .profiles.https
  | type == "object"
    and .commandName == "Project"
    and (.environmentVariables | type == "object")
    and .environmentVariables.ASPNETCORE_ENVIRONMENT == "Development"
    and .environmentVariables.DOTNET_ENVIRONMENT == "Development"
    and .applicationUrl == "https://localhost:17129"
    and .environmentVariables.ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL == "https://localhost:21129"
    and .environmentVariables.ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL == "https://localhost:22129"
    and .environmentVariables.ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL == "https://localhost:23129"
' "$launch_settings" >/dev/null; then
  printf 'AppHost launch-profile contract failed: profiles.https must use secure localhost endpoints and both development environment variables\n' >&2
  exit 1
fi

if grep -Eq 'https?://(0\.0\.0\.0|[^/";]*\.local)' "$launch_settings"; then
  printf 'AppHost launch-profile contract failed: profile contains a certificate-hostname-unsafe endpoint\n' >&2
  exit 1
fi

required_asset_paths=(
  '../../../deploy/postgres/init'
  '../../../deploy/observability/otel-collector.yml'
  '../../../deploy/observability/loki.yml'
  '../../../deploy/observability/tempo.yml'
  '../../../deploy/observability/prometheus.yml'
  '../../../deploy/observability/grafana'
  '../../../web/apps/web'
)

for required_path in "${required_asset_paths[@]}"; do
  if ! grep -Fq "\"$required_path\"" "$apphost_program"; then
    printf 'AppHost asset-path contract failed: %s does not reference %s\n' "$apphost_program" "$required_path" >&2
    exit 1
  fi
  if [[ ! -e "$(dirname "$apphost_project")/$required_path" ]]; then
    printf 'AppHost asset-path contract failed: %s does not resolve from the AppHost directory\n' "$required_path" >&2
    exit 1
  fi
done
