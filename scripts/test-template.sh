#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-template.XXXXXX")
test_root=$(cd "$test_root" && pwd -P)
trap 'rm -rf "$test_root"' EXIT
template_hive="$test_root/template-hive"
template_manifest="$repository_root/templates/trykatch/.template.config/template.json"

fail() {
  printf 'Template contract failed: %s\n' "$1" >&2
  exit 1
}

legacy_brand_pattern='([nN][aA][nN][oO].{0,24}[bB][oO][iI][lL][eE][rR][pP][lL][aA][tT][eE])|([aA][sS][pP].{0,12}[nN][aA][nN][oO])'
if git -C "$repository_root" grep -n -I -E "$legacy_brand_pattern" -- .; then
  fail 'tracked source contains legacy product branding'
fi
grep -Fq '"identity": "Trykatch.Templates.Enterprise"' "$template_manifest" ||
  fail 'the template identity is not owned by Trykatch'
grep -Fq '"groupIdentity": "Trykatch.Templates"' "$template_manifest" ||
  fail 'the template group identity is not owned by Trykatch'
grep -Fq '"author": "Trykatch contributors"' "$template_manifest" ||
  fail 'the template author is not Trykatch contributors'

dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$test_root/package"
package_path=$(find "$test_root/package" -name 'Trykatch.Templates.*.nupkg' -print -quit)
dotnet new --debug:custom-hive "$template_hive" install "$package_path" --force
dotnet pack \
  "$repository_root/templates/trykatch/tools/Trykatch.ModuleTool/Trykatch.ModuleTool.csproj" \
  -c Release \
  -o "$test_root/package" \
  -p:PackageVersion=0.1.0-ci
dotnet tool install \
  Trykatch.Cli \
  --version 0.1.0-ci \
  --tool-path "$test_root/tools" \
  --add-source "$test_root/package"
"$test_root/tools/trykatch" --help >/dev/null
help_output=$("$test_root/tools/trykatch" help)
grep -Fq 'Create an application:' <<<"$help_output" ||
  fail 'CLI help does not explain application creation'
grep -Fq 'trykatch new <name> [options]' <<<"$help_output" ||
  fail 'CLI help does not show the application creation command'
grep -Fq 'trykatch template install' <<<"$help_output" ||
  fail 'CLI help does not show the progress-aware template installer'
grep -Fq 'trykatch template uninstall' <<<"$help_output" ||
  fail 'CLI help does not show template removal'
grep -Fq 'trykatch update' <<<"$help_output" ||
  fail 'CLI help does not show the template update command'
grep -Fq 'trykatch start' <<<"$help_output" ||
  fail 'CLI help does not show the application start command'
grep -Fq -- '--ui <react|none>' <<<"$help_output" ||
  fail 'CLI help does not document the frontend choice'
grep -Fq 'Module lifecycle:' <<<"$help_output" ||
  fail 'CLI help does not document module lifecycle commands'
module_help_output=$("$test_root/tools/trykatch" module help)
grep -Fq 'trykatch module doctor' <<<"$module_help_output" ||
  fail 'module help does not document workspace validation'
grep -Fq 'trykatch module remove <id>' <<<"$module_help_output" ||
  fail 'module help does not document the remove alias'
nested_help_output=$("$test_root/tools/trykatch" module list --help)
grep -Fq 'Trykatch module lifecycle' <<<"$nested_help_output" ||
  fail 'nested module commands do not support --help'
template_help_output=$("$test_root/tools/trykatch" template help)
grep -Fq 'trykatch template install [--version <version>] [--force]' <<<"$template_help_output" ||
  fail 'template help does not document installation options'
grep -Fq 'trykatch template update [--version <version>]' <<<"$template_help_output" ||
  fail 'template help does not document update options'
grep -Fq 'trykatch template uninstall' <<<"$template_help_output" ||
  fail 'template help does not document template removal'
start_help_output=$("$test_root/tools/trykatch" start --help)
grep -Fq 'trykatch start [--root <path>]' <<<"$start_help_output" ||
  fail 'start help does not document AppHost discovery'
new_help_output=$("$test_root/tools/trykatch" new --help)
grep -Fq 'trykatch new <name> [options]' <<<"$new_help_output" ||
  fail 'new help does not document application generation'
grep -Fq 'initialized as a Git repository on the main branch' <<<"$new_help_output" ||
  fail 'new help does not explain Git initialization'
cli_informational_version=$(dotnet "$(find "$test_root/tools/.store/trykatch.cli/0.1.0-ci" -name 'Trykatch.ModuleTool.dll' -print -quit)" --version 2>/dev/null || true)
test "$cli_informational_version" = 'Trykatch CLI 0.1.0-ci' ||
  fail "packaged CLI reports '$cli_informational_version' instead of its package version"

generate_and_build() {
  local name=$1
  shift
  local namespace_name=${name//-/.}
  local output="$test_root/$namespace_name"
  dotnet new --debug:custom-hive "$template_hive" trykatch -n "$name" -o "$output" "$@" --allow-scripts yes
  test -d "$output/.git" || fail "generated application '$name' was not initialized as a Git repository"
  test "$(git -C "$output" branch --show-current)" = main ||
    fail "generated application '$name' did not use main as its initial Git branch"
  dotnet restore "$output/$namespace_name.slnx"
  dotnet build "$output/$namespace_name.slnx" --no-restore
  dotnet run --project "$output/tools/$namespace_name.ModuleTool" --no-build -- module doctor --root "$output"
  TRYKATCH_RELEASE_VERSION=ci-validation docker compose --env-file "$output/.env.example" -f "$output/compose.yml" config --quiet
  test -f "$output/deploy/observability/otel-collector.tls.yml"
  test -f "$output/compose.observability-tls.yml"
  test -f "$output/scripts/test-observability.sh"
  if [[ -f "$output/web/package.json" ]]; then
    bash "$output/scripts/test-proxy-headers.sh"
    (
      cd "$output/web"
      corepack pnpm install --frozen-lockfile
      corepack pnpm typecheck
      corepack pnpm build
    )
  fi
  if [[ $namespace_name == Horizon || $namespace_name == Trykatch ]]; then
    dotnet test "$output/tests/$namespace_name.UnitTests/$namespace_name.UnitTests.csproj" --no-build \
      --filter 'GeneratedApplicationsKeepTheirPreviewNineEventName|MovedIntegrationEventsKeepTheirExistingWireContractNames'
    dotnet test "$output/tests/$namespace_name.IntegrationTests/$namespace_name.IntegrationTests.csproj" --no-build \
      --filter PreviewNine
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
bash "$repository_root/scripts/test-apphost-launch-profile.sh" "$test_root/Horizon"
grep -Fq 'AddViteApp("web", "../../../web/apps/web")' "$test_root/Horizon/src/API/Horizon.AppHost/Program.cs" ||
  fail 'React template output does not register the Vite application with AppHost'
"$test_root/tools/trykatch" module doctor --root "$test_root/Horizon"
generate_and_build Acme.Tools-Portal --ui none
generate_and_build Trykatch --ui none
test ! -e "$test_root/Acme.Tools.Portal/web"
test ! -e "$test_root/Acme.Tools.Portal/.github/workflows/web.yml"
test ! -e "$test_root/Acme.Tools.Portal/compose.backend.yml"
test ! -e "$test_root/Acme.Tools.Portal/README.backend.md"
test ! -e "$test_root/Acme.Tools.Portal/scripts/test-proxy-headers.sh"
test -f "$test_root/Acme.Tools.Portal/compose.yml"
test -f "$test_root/Acme.Tools.Portal/README.md"
if grep -Fq 'AddViteApp(' "$test_root/Acme.Tools.Portal/src/API/Acme.Tools.Portal.AppHost/Program.cs"; then
  fail 'Backend-only template output registers a Vite application'
fi
grep -Fq 'ports: ["8080:8080"]' "$test_root/Acme.Tools.Portal/compose.yml"
generate_and_build Email.Sample --ui none --email
generate_and_build Storage.Sample --ui none --storage
generate_and_build Documents.Sample --ui none --documents
generate_and_build Images.Sample --ui none --images
generate_and_build Everything.Sample --ui none --email --storage --documents --images
