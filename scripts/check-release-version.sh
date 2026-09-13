#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

release_version="$(tr -d '[:space:]' < RELEASE_VERSION)"
expected_version="${1:-}"
failure_count=0

fail() {
  printf 'release-version error: %s\n' "$1" >&2
  failure_count=$((failure_count + 1))
}

require_text() {
  local path="$1"
  local expected="$2"

  if ! rg --fixed-strings --quiet "$expected" "$path"; then
    fail "$path must contain: $expected"
  fi
}

if [[ ! "$release_version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
  fail "RELEASE_VERSION is not a valid semantic version: $release_version"
fi

if [[ -n "$expected_version" && "$release_version" != "$expected_version" ]]; then
  fail "release tag version $expected_version does not match RELEASE_VERSION $release_version"
fi

require_text Trykatch.Templates.csproj "<PackageVersion>$release_version</PackageVersion>"
require_text templates/trykatch/tools/Trykatch.ModuleTool/Trykatch.ModuleTool.csproj "<PackageVersion>$release_version</PackageVersion>"
require_text templates/trykatch/tests/Trykatch.UnitTests/TemplatePackageInstallerTests.cs \
  "TemplatePackageInstaller.CurrentVersion.ShouldBe(\"$release_version\")"
require_text templates/trykatch/tools/Trykatch.ModuleTool/TemplatePackageInstaller.cs \
  "for example $release_version."
require_text docs/production-readiness-plan.md \
  "Current published preview package: \`$release_version\`."

current_installation_surfaces=(
  README.md
  docs-site/src/content/docs/getting-started/install.mdx
  docs-site/src/content/docs/fr/getting-started/install.mdx
  templates/trykatch/web/apps/web/src/views/LandingPage.tsx
)

require_text README.md "dotnet tool install --global Trykatch.Cli --version $release_version"
require_text README.md "dotnet new install Trykatch.Templates@$release_version"
require_text docs-site/src/content/docs/getting-started/install.mdx \
  "dotnet tool install --global Trykatch.Cli --version $release_version"
require_text docs-site/src/content/docs/getting-started/install.mdx \
  "dotnet tool update --global Trykatch.Cli --version $release_version"
require_text docs-site/src/content/docs/getting-started/install.mdx \
  "dotnet new install Trykatch.Templates@$release_version"
require_text docs-site/src/content/docs/fr/getting-started/install.mdx \
  "dotnet tool install --global Trykatch.Cli --version $release_version"
require_text docs-site/src/content/docs/fr/getting-started/install.mdx \
  "dotnet tool update --global Trykatch.Cli --version $release_version"
require_text docs-site/src/content/docs/fr/getting-started/install.mdx \
  "dotnet new install Trykatch.Templates@$release_version"
require_text templates/trykatch/web/apps/web/src/views/LandingPage.tsx \
  "dotnet new install Trykatch.Templates@$release_version"

while IFS= read -r reference; do
  if [[ "$reference" != *"$release_version"* ]]; then
    fail "current installation command is stale: $reference"
  fi
done < <(
  rg --line-number \
    'dotnet tool (install|update).*Trykatch\.Cli --version|dotnet new install .*Trykatch\.Templates|Trykatch\.Templates\.[0-9][^[:space:]]*\.nupkg' \
    "${current_installation_surfaces[@]}" || true
)

if (( failure_count > 0 )); then
  printf 'Release-version contract failed with %d error(s).\n' "$failure_count" >&2
  exit 1
fi

printf 'Release-version contract passed for %s.\n' "$release_version"
