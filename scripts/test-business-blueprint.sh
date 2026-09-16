#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
blueprint_test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-blueprint.XXXXXX")
blueprint_test_root=$(cd "$blueprint_test_root" && pwd -P)
mode=${1:-backend}
cli_version="0.1.0-ci.$(basename "$blueprint_test_root")"
case "$mode" in backend|web) ;; *) printf 'Use backend or web.\n' >&2; exit 2 ;; esac
printf 'Blueprint acceptance workspace: %s\n' "$blueprint_test_root"
# Retain failures for diagnosis. The directory is private and uniquely allocated.
cleanup() {
  local result=$?
  if [[ $result == 0 && ${TRYKATCH_KEEP_BLUEPRINT_WORKSPACE:-false} != true ]]; then
    rm -rf "$blueprint_test_root"
  else
    printf 'Blueprint workspace retained: %s\n' "$blueprint_test_root"
  fi
}
trap cleanup EXIT

dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$blueprint_test_root/packages"
dotnet new install "$blueprint_test_root"/packages/Trykatch.Templates.*.nupkg --debug:custom-hive "$blueprint_test_root/hive"
dotnet new trykatch -n BlueprintAcceptance -o "$blueprint_test_root/app" --allow-scripts yes --debug:custom-hive "$blueprint_test_root/hive"
dotnet pack "$repository_root/templates/trykatch/tools/Trykatch.ModuleTool" -c Release -o "$blueprint_test_root/packages" -p:PackageVersion="$cli_version"
dotnet tool install Trykatch.Cli --version "$cli_version" --tool-path "$blueprint_test_root/tools" --add-source "$blueprint_test_root/packages"
cd "$blueprint_test_root/app"
"$blueprint_test_root/tools/trykatch" module create --help > "$blueprint_test_root/create-help.txt"
grep -q -- '--blueprint' "$blueprint_test_root/create-help.txt"
grep -q -- '--with-web' "$blueprint_test_root/create-help.txt"
grep -q 'Live progress shows the current step and elapsed time' "$blueprint_test_root/create-help.txt"
if "$blueprint_test_root/tools/trykatch" module validate --blueprint blueprints/shipment-reception.json --fields name:string; then
  printf 'CLI incorrectly accepted mutually exclusive blueprint/fields arguments.\n' >&2
  exit 1
fi
"$blueprint_test_root/tools/trykatch" module validate --blueprint blueprints/shipment-reception.json
if [[ $mode == web ]]; then
  "$blueprint_test_root/tools/trykatch" module create ShipmentReceptions --blueprint blueprints/shipment-reception.json --with-web | tee "$blueprint_test_root/create-progress.txt"
else
  "$blueprint_test_root/tools/trykatch" module create ShipmentReceptions --blueprint blueprints/shipment-reception.json | tee "$blueprint_test_root/create-progress.txt"
fi
grep -q 'Restoring .NET dependencies' "$blueprint_test_root/create-progress.txt"
grep -q 'Running generated unit tests' "$blueprint_test_root/create-progress.txt"
grep -q 'Checking module health with doctor' "$blueprint_test_root/create-progress.txt"
grep -q 'Module generation completed in ' "$blueprint_test_root/create-progress.txt"
if [[ $mode == web ]]; then
  grep -q 'Installing frontend dependencies' "$blueprint_test_root/create-progress.txt"
  grep -q 'Building the frontend' "$blueprint_test_root/create-progress.txt"
fi
if LC_ALL=C grep -q $'\r' "$blueprint_test_root/create-progress.txt"; then
  printf 'Redirected creation progress must not contain terminal carriage returns.\n' >&2
  exit 1
fi
cp blueprints/tests/ShipmentBlueprintAcceptanceTests.cs.fixture tests/BlueprintAcceptance.IntegrationTests/ShipmentBlueprintAcceptanceTests.cs
dotnet test tests/BlueprintAcceptance.IntegrationTests --filter 'FullyQualifiedName~ShipmentBlueprintAcceptanceTests|FullyQualifiedName~EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole'
"$blueprint_test_root/tools/trykatch" module doctor
printf 'Blueprint %s acceptance passed.\n' "$mode"
