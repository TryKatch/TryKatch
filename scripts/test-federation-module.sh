#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
template_root="$repository_root/templates/flatpack"
tool="$template_root/tools/FlatpackApp.ModuleTool/FlatpackApp.ModuleTool.csproj"
snapshot_root=$(mktemp -d)
tracked_outputs=(
  "flatpack.modules.json"
  "flatpack.modules.lock.json"
  "src/FlatpackApp.Api/Modules/EnabledModules.cs"
  "src/FlatpackApp.Migrator/Modules/EnabledModules.cs"
  "web/apps/web/src/modules.ts"
  "web/packages/api-client/openapi/FlatpackApp.Api.json"
  "web/packages/api-client/src/generated/flatpack.ts"
  "docs/generated/assistant-contract.json"
)

for output in "${tracked_outputs[@]}"; do
  mkdir -p "$snapshot_root/$(dirname "$output")"
  cp "$template_root/$output" "$snapshot_root/$output"
done

restore_default() {
  dotnet run --project "$tool" -c Release --no-build -- module disable federation --root "$template_root" >/dev/null || true
}

cleanup() {
  restore_default
  rm -rf "$snapshot_root"
}
trap cleanup EXIT

dotnet run --project "$tool" -c Release --no-build -- module enable federation --root "$template_root"
dotnet build "$template_root/src/FlatpackApp.Api/FlatpackApp.Api.csproj" -c Release --no-restore -warnaserror
(cd "$template_root/web" && pnpm typecheck)

restore_default
dotnet build "$template_root/src/FlatpackApp.Api/FlatpackApp.Api.csproj" -c Release --no-restore -warnaserror
(cd "$template_root/web" && pnpm generate)
dotnet run --project "$tool" -c Release --no-build -- module doctor --root "$template_root"

for output in "${tracked_outputs[@]}"; do
  diff --unified "$snapshot_root/$output" "$template_root/$output"
done
