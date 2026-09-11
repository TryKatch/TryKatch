#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
project_file="$repository_root/Trykatch.Templates.csproj"
template_root="$repository_root/templates/trykatch"
template_manifest="$template_root/.template.config/template.json"
docker_ignore="$template_root/.dockerignore"

fail() {
  printf 'Artifact boundary contract failed: %s\n' "$1" >&2
  exit 1
}

for pattern in \
  'templates/**/.git/**' \
  'templates/**/.idea/**' \
  'templates/**/.vscode/**' \
  'templates/**/.vercel/**' \
  'templates/**/.env' \
  'templates/**/.env.*' \
  'templates/**/secrets/**' \
  'templates/**/*.pfx' \
  'templates/**/*.p12' \
  'templates/**/*.pem' \
  'templates/**/*.key'; do
  grep -Fq "$pattern" "$project_file" || fail "NuGet packing does not exclude $pattern"
done

jq -e '
  .sources[0].exclude as $excluded |
  [".git/**", ".idea/**", ".vscode/**", ".vercel/**", "**/.env", "**/.env.*", "**/secrets/**", "**/*.pfx", "**/*.p12", "**/*.pem", "**/*.key"] |
  all(. as $pattern | $excluded | index($pattern) != null)
' "$template_manifest" >/dev/null || fail 'template generation is missing a credential exclusion'

for pattern in '**/.env' '**/.env.*' '**/secrets/' '**/*.pfx' '**/*.p12' '**/*.pem' '**/*.key'; do
  grep -Fxq "$pattern" "$docker_ignore" || fail "Docker context does not exclude $pattern"
done
grep -Fxq '!.env.example' "$docker_ignore" || fail 'Docker context does not explicitly retain the vetted root environment example'

printf 'Artifact boundary contract passed.\n'

if [[ ${1:-} != --runtime ]]; then
  exit 0
fi

for command in docker dotnet jq unzip; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-artifacts.XXXXXX")
template_hive="$test_root/template-hive"
source_template_hive="$test_root/source-template-hive"
context_output="$test_root/context"
sentinel_root="$template_root/.artifact-boundary-tests"
environment_sentinel="$template_root/.env.trykatch-artifact-boundary-test"
secret_sentinel="$template_root/secrets/trykatch-artifact-boundary-test.pfx"
nested_example_sentinel="$template_root/secrets/.env.example"
key_sentinel="$sentinel_root/private.key"
cleanup() {
  rm -f "$environment_sentinel" "$secret_sentinel" "$nested_example_sentinel" "$key_sentinel"
  rmdir "$sentinel_root" "$template_root/secrets" 2>/dev/null || true
  rm -rf "$test_root"
}

for sentinel in "$environment_sentinel" "$secret_sentinel" "$nested_example_sentinel" "$key_sentinel"; do
  [[ ! -e $sentinel ]] || fail "refusing to overwrite existing path $sentinel"
done
trap cleanup EXIT
mkdir -p "$template_root/secrets" "$sentinel_root"

printf 'artifact-boundary-sentinel\n' >"$environment_sentinel"
printf 'artifact-boundary-sentinel\n' >"$secret_sentinel"
printf 'artifact-boundary-sentinel\n' >"$nested_example_sentinel"
printf 'artifact-boundary-sentinel\n' >"$key_sentinel"

dotnet new --debug:custom-hive "$source_template_hive" install "$template_root" --force >/dev/null
dotnet new --debug:custom-hive "$source_template_hive" trykatch -n ArtifactBoundary.Source -o "$test_root/generated-source" --ui none --allow-scripts yes >/dev/null
for sentinel in '.env.trykatch-artifact-boundary-test' 'secrets/trykatch-artifact-boundary-test.pfx' 'secrets/.env.example' '.artifact-boundary-tests/private.key'; do
  [[ ! -e "$test_root/generated-source/$sentinel" ]] || fail "source-generated application contains excluded sentinel $sentinel"
done
[[ -f "$test_root/generated-source/.env.example" ]] || fail 'source-generated application lost the root environment example'

dotnet pack "$project_file" -c Release -o "$test_root/package" >/dev/null
package_path=$(find "$test_root/package" -name 'Trykatch.Templates.*.nupkg' -print -quit)
[[ -n $package_path ]] || fail 'fixture template package was not produced'
package_entries=$(unzip -Z1 "$package_path")
for sentinel in '.env.trykatch-artifact-boundary-test' 'secrets/trykatch-artifact-boundary-test.pfx' 'secrets/.env.example' '.artifact-boundary-tests/private.key'; do
  if grep -Fq "$sentinel" <<<"$package_entries"; then
    fail "NuGet package contains excluded sentinel $sentinel"
  fi
done
grep -Eq '(^|/)\.env\.example$' <<<"$package_entries" || fail 'NuGet package lost the environment example'

dotnet new --debug:custom-hive "$template_hive" install "$package_path" --force >/dev/null
dotnet new --debug:custom-hive "$template_hive" trykatch -n ArtifactBoundary.Sample -o "$test_root/generated" --ui none --allow-scripts yes >/dev/null
for sentinel in '.env.trykatch-artifact-boundary-test' 'secrets/trykatch-artifact-boundary-test.pfx' 'secrets/.env.example' '.artifact-boundary-tests/private.key'; do
  [[ ! -e "$test_root/generated/$sentinel" ]] || fail "generated application contains excluded sentinel $sentinel"
done
[[ -f "$test_root/generated/.env.example" ]] || fail 'generated application lost the environment example'

probe_dockerfile="$test_root/Dockerfile.artifact-boundary"
printf 'FROM scratch\nCOPY . /\n' >"$probe_dockerfile"
docker buildx build --file "$probe_dockerfile" --output "type=local,dest=$context_output" "$template_root" >/dev/null
for sentinel in '.env.trykatch-artifact-boundary-test' 'secrets/trykatch-artifact-boundary-test.pfx' 'secrets/.env.example' '.artifact-boundary-tests/private.key'; do
  [[ ! -e "$context_output/$sentinel" ]] || fail "Docker context contains excluded sentinel $sentinel"
done
[[ -f "$context_output/.env.example" ]] || fail 'Docker context lost the environment example'

printf 'Artifact boundary runtime behavior passed.\n'
