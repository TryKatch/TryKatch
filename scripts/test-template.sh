#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-template.XXXXXX")
test_root=$(cd "$test_root" && pwd -P)
trap 'rm -rf "$test_root"' EXIT
template_hive="$test_root/template-hive"

dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$test_root/package"
package_path=$(find "$test_root/package" -name 'Trykatch.Templates.*.nupkg' -print -quit)
dotnet new --debug:custom-hive "$template_hive" install "$package_path" --force
dotnet pack \
  "$repository_root/templates/trykatch/tools/TrykatchApp.ModuleTool/TrykatchApp.ModuleTool.csproj" \
  -c Release \
  -o "$test_root/package" \
  -p:PackageVersion=0.1.0-ci
dotnet tool install \
  Trykatch.Cli \
  --version 0.1.0-ci \
  --tool-path "$test_root/tools" \
  --add-source "$test_root/package"
"$test_root/tools/trykatch" --help >/dev/null

generate_and_build() {
  local name=$1
  shift
  local namespace_name=${name//-/.}
  local output="$test_root/$namespace_name"
  dotnet new --debug:custom-hive "$template_hive" trykatch -n "$name" -o "$output" "$@"
  dotnet restore "$output/$namespace_name.slnx"
  dotnet build "$output/$namespace_name.slnx" --no-restore
  dotnet run --project "$output/tools/$namespace_name.ModuleTool" --no-build -- module doctor --root "$output"
  if [[ -f "$output/web/package.json" ]]; then
    (
      cd "$output/web"
      corepack pnpm install --frozen-lockfile
      corepack pnpm typecheck
      corepack pnpm build
    )
  fi
  # Each generated solution can produce several gigabytes of runtime assets.
  # Retain the generated source for assertions, but release build intermediates
  # before exercising the next template permutation on constrained CI runners.
  find "$output" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
}

generate_and_build Horizon
test -d "$test_root/Horizon/web"
test -f "$test_root/Horizon/.github/workflows/web.yml"
test ! -e "$test_root/Horizon/compose.backend.yml"
test ! -e "$test_root/Horizon/README.backend.md"
"$test_root/tools/trykatch" module doctor --root "$test_root/Horizon"
generate_and_build Acme.Tools-Portal --ui none
test ! -e "$test_root/Acme.Tools.Portal/web"
test ! -e "$test_root/Acme.Tools.Portal/.github/workflows/web.yml"
test ! -e "$test_root/Acme.Tools.Portal/compose.backend.yml"
test ! -e "$test_root/Acme.Tools.Portal/README.backend.md"
test -f "$test_root/Acme.Tools.Portal/compose.yml"
test -f "$test_root/Acme.Tools.Portal/README.md"
grep -Fq 'ports: ["8080:8080"]' "$test_root/Acme.Tools.Portal/compose.yml"
generate_and_build Email.Sample --ui none --email
generate_and_build Storage.Sample --ui none --storage
generate_and_build Documents.Sample --ui none --documents
generate_and_build Images.Sample --ui none --images
generate_and_build Everything.Sample --ui none --email --storage --documents --images
