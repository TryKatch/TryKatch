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

grep -Fq '"https"' "$launch_settings" || {
  printf 'AppHost launch-profile contract failed: secure https profile is missing\n' >&2
  exit 1
}
grep -Fq '"commandName": "Project"' "$launch_settings" || {
  printf 'AppHost launch-profile contract failed: profile does not launch the AppHost project\n' >&2
  exit 1
}
grep -Fq '"ASPNETCORE_ENVIRONMENT": "Development"' "$launch_settings" || {
  printf 'AppHost launch-profile contract failed: ASP.NET Core development environment is missing\n' >&2
  exit 1
}
grep -Fq '"DOTNET_ENVIRONMENT": "Development"' "$launch_settings" || {
  printf 'AppHost launch-profile contract failed: .NET development environment is missing\n' >&2
  exit 1
}

if grep -Eq 'https?://(0\.0\.0\.0|[^/";]*\.local)' "$launch_settings"; then
  printf 'AppHost launch-profile contract failed: profile contains a certificate-hostname-unsafe endpoint\n' >&2
  exit 1
fi
