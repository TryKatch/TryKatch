#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
template_root="$repository_root/templates/trykatch"
tool="$template_root/tools/TrykatchApp.ModuleTool/TrykatchApp.ModuleTool.csproj"
snapshot_root=$(mktemp -d)
tracked_outputs=(
  "trykatch.modules.json"
  "trykatch.modules.lock.json"
  "src/TrykatchApp.Api/Modules/EnabledModules.cs"
  "src/TrykatchApp.Migrator/Modules/EnabledModules.cs"
  "web/apps/web/src/modules.ts"
  "web/packages/api-client/openapi/TrykatchApp.Api.json"
  "web/packages/api-client/src/generated/trykatch.ts"
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
dotnet build "$template_root/src/TrykatchApp.Api/TrykatchApp.Api.csproj" -c Release --no-restore -warnaserror
(cd "$template_root/web" && pnpm typecheck)

restore_default
dotnet build "$template_root/src/TrykatchApp.Api/TrykatchApp.Api.csproj" -c Release --no-restore -warnaserror
(cd "$template_root/web" && pnpm generate)
dotnet run --project "$tool" -c Release --no-build -- module doctor --root "$template_root"

for output in "${tracked_outputs[@]}"; do
  diff --unified "$snapshot_root/$output" "$template_root/$output"
done
