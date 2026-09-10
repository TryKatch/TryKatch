#!/usr/bin/env bash
set -euo pipefail

application_root=${1:?"usage: test-apphost-launch-profile.sh <generated-application-root>"}
apphost_project=$(find "$application_root/src" -mindepth 2 -maxdepth 2 -type f -name '*.AppHost.csproj' -print -quit)

if [[ -z "$apphost_project" ]]; then
  printf 'AppHost launch-profile contract failed: no AppHost project found under %s/src\n' "$application_root" >&2
  exit 1
fi

launch_settings="$(dirname "$apphost_project")/Properties/launchSettings.json"
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
' "$launch_settings" >/dev/null; then
  printf 'AppHost launch-profile contract failed: profiles.https must be a valid Project profile with both development environment variables\n' >&2
  exit 1
fi

if grep -Eq 'https?://(0\.0\.0\.0|[^/";]*\.local)' "$launch_settings"; then
  printf 'AppHost launch-profile contract failed: profile contains a certificate-hostname-unsafe endpoint\n' >&2
  exit 1
fi
