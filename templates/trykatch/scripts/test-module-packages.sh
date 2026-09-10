#!/usr/bin/env bash
set -euo pipefail

workspace_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_root="$(mktemp -d)"
trap 'rm -rf "$package_root"' EXIT

for module in Projects Documents Federation; do
  project="$workspace_root/src/Modules/$module/Trykatch.Modules.$module.Infrastructure/Trykatch.Modules.$module.Infrastructure.csproj"
  dotnet pack "$project" -c Release --no-restore -o "$package_root"
  package="$package_root/Trykatch.Modules.$module.1.0.0.nupkg"
  entries="$(unzip -Z1 "$package")"

  for layer in Domain Application IntegrationEvents Presentation Infrastructure; do
    expected="lib/net10.0/Trykatch.Modules.$module.$layer.dll"
    if ! grep -Fxq "$expected" <<<"$entries"; then
      echo "Composite module package is missing $expected" >&2
      exit 1
    fi
  done

  unexpected="$(grep -E '^lib/net10\.0/Trykatch\.Modules\..*\.dll$' <<<"$entries" | grep -v -E "^lib/net10\.0/Trykatch\.Modules\.$module\.(Domain|Application|IntegrationEvents|Presentation|Infrastructure)\.dll$" || true)"
  if [[ -n "$unexpected" ]]; then
    echo "Composite module package contains assemblies outside the $module boundary:" >&2
    echo "$unexpected" >&2
    exit 1
  fi
done

echo "All composite module packages contain exactly their five module assemblies."
