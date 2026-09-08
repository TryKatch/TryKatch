#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/flatpack-template.XXXXXX")
test_root=$(cd "$test_root" && pwd -P)
trap 'rm -rf "$test_root"' EXIT

dotnet pack "$repository_root/Flatpack.Templates.csproj" -c Release -o "$test_root/package"
package_path=$(find "$test_root/package" -name 'Flatpack.Templates.*.nupkg' -print -quit)
dotnet new install "$package_path" --force
trap 'dotnet new uninstall Flatpack.Templates >/dev/null 2>&1 || true; rm -rf "$test_root"' EXIT

generate_and_build() {
  local name=$1
  shift
  local namespace_name=${name//-/.}
  local output="$test_root/$namespace_name"
  dotnet new flatpack -n "$name" -o "$output" "$@"
  dotnet restore "$output/$namespace_name.slnx"
  dotnet build "$output/$namespace_name.slnx" --no-restore
}

generate_and_build Horizon
test -d "$test_root/Horizon/web"
test -f "$test_root/Horizon/.github/workflows/web.yml"
test ! -e "$test_root/Horizon/compose.backend.yml"
test ! -e "$test_root/Horizon/README.backend.md"
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
