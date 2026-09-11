#!/usr/bin/env bash
set -euo pipefail

workspace_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_root="$(mktemp -d)"
trap 'rm -rf "$package_root"' EXIT

shopt -s nullglob
module_manifests=("$workspace_root"/src/Modules/*/trykatch.module.json)
if [[ "${#module_manifests[@]}" -eq 0 ]]; then
  echo "No module manifests were discovered." >&2
  exit 1
fi

for manifest in "${module_manifests[@]}"; do
  module_directory="$(dirname "$manifest")"
  module="$(basename "$module_directory")"
  project="$workspace_root/src/Modules/$module/Trykatch.Modules.$module.Infrastructure/Trykatch.Modules.$module.Infrastructure.csproj"
  for version in 1.0.0 1.1.0; do
    mkdir -p "$package_root/$version"
    dotnet pack "$project" -c Release --no-restore -p:Version="$version" -o "$package_root/$version"
  done
  package="$package_root/1.0.0/Trykatch.Modules.$module.1.0.0.nupkg"
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

  consumer="$package_root/consumer-$module"
  mkdir -p "$consumer"
  cat > "$consumer/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Trykatch.Modules.$module" Version="1.0.0" />
  </ItemGroup>
</Project>
EOF
  cat > "$consumer/Program.cs" <<EOF
using System.Reflection;

string assemblyPath = Path.Combine(AppContext.BaseDirectory, "Trykatch.Modules.$module.Infrastructure.dll");
if (!File.Exists(assemblyPath))
    throw new InvalidOperationException("Restored composite module entry assembly is missing.");
AssemblyName name = AssemblyName.GetAssemblyName(assemblyPath);
if (!string.Equals(name.Name, "Trykatch.Modules.$module.Infrastructure", StringComparison.Ordinal))
    throw new InvalidOperationException("Restored composite module assembly identity is invalid.");
EOF
  cat > "$consumer/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="module-under-test" value="$package_root/1.0.0" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="module-under-test">
      <package pattern="Trykatch.Modules.$module" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
  dotnet restore "$consumer/Consumer.csproj" --configfile "$consumer/NuGet.Config" --packages "$consumer/.packages"
  dotnet build "$consumer/Consumer.csproj" -c Release --no-restore
  dotnet run --project "$consumer/Consumer.csproj" -c Release --no-build --no-restore
done

TRYKATCH_ACTUAL_MODULE_PACKAGES="$package_root" \
TRYKATCH_ACTUAL_MODULE_SOURCE_ROOT="$workspace_root/src/Modules" dotnet test \
  "$workspace_root/tests/Trykatch.UnitTests/Trykatch.UnitTests.csproj" \
  -c Release --no-restore --filter FullyQualifiedName~BuiltCompositePackagesCompleteThePackageLifecycle \
  -p:TreatWarningsAsErrors=true

echo "All discovered composite module packages contain exactly five module assemblies, restore in an isolated consumer, and pass dependency-aware install, upgrade, unregister, and actual-source eject lifecycle verification."
