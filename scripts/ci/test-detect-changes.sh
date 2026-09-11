#!/usr/bin/env bash
set -euo pipefail

script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
detector="$script_root/detect-changes.sh"

assert_scope() {
  local path=$1
  local scope=$2
  local expected=$3
  local actual

  actual=$(printf '%s\n' "$path" | bash "$detector" --paths | sed -n "s/^${scope}=//p")
  if [[ $actual != "$expected" ]]; then
    printf 'Expected %s=%s for %s, received %s\n' "$scope" "$expected" "$path" "$actual" >&2
    exit 1
  fi
}

assert_scope 'docs-site/src/content/docs/en/index.mdx' docs true
assert_scope 'docs-site/src/content/docs/en/index.mdx' template false
assert_scope 'templates/trykatch/src/API/Trykatch.Api/Program.cs' backend true
assert_scope 'templates/trykatch/src/API/Trykatch.Api/Program.cs' web false
assert_scope 'templates/trykatch/.editorconfig' backend true
assert_scope 'templates/trykatch/.editorconfig' qualification true
assert_scope 'templates/trykatch/.future-template-setting' docs true
assert_scope 'templates/trykatch/.future-template-setting' backend true
assert_scope 'templates/trykatch/.future-template-setting' web true
assert_scope 'templates/trykatch/.future-template-setting' observability true
assert_scope 'templates/trykatch/.future-template-setting' deployment true
assert_scope 'templates/trykatch/.future-template-setting' packaging true
assert_scope 'templates/trykatch/.future-template-setting' qualification true
assert_scope 'templates/trykatch/web/apps/web/src/main.tsx' web true
assert_scope 'templates/trykatch/src/Modules/Federation/Web/src/index.tsx' web true
assert_scope 'templates/trykatch/src/Modules/Federation/Web/src/index.tsx' backend false
assert_scope 'templates/trykatch/src/Modules/Federation/Web/src/index.tsx' qualification true
assert_scope 'templates/trykatch/deploy/observability/tempo.yml' observability true
assert_scope 'templates/trykatch/compose.yml' deployment true
assert_scope 'templates/trykatch/compose.yml' observability true
assert_scope 'templates/trykatch/.template.config/template.json' packaging true
assert_scope 'templates/trykatch/src/API/Trykatch.Api/Program.cs' qualification true
assert_scope 'templates/trykatch/web/apps/web/src/main.tsx' qualification true
assert_scope 'templates/trykatch/web/apps/web/nginx.conf' web true
assert_scope 'templates/trykatch/web/apps/web/nginx.conf' deployment true
assert_scope 'templates/trykatch/web/apps/web/nginx.conf' qualification true
assert_scope 'templates/trykatch/compose.yml' qualification true
assert_scope 'templates/trykatch/deploy/postgres/init/10-create-migrator.sql' deployment true
assert_scope 'templates/trykatch/deploy/postgres/init/10-create-migrator.sql' qualification true
assert_scope 'templates/trykatch/scripts/test-proxy-headers.sh' deployment true
assert_scope 'templates/trykatch/scripts/test-proxy-headers.sh' web true
assert_scope 'templates/trykatch/scripts/test-proxy-headers.sh' qualification true
assert_scope 'templates/trykatch/.template.config/template.json' qualification true
assert_scope 'templates/trykatch/deploy/observability/tempo.yml' qualification false
assert_scope 'scripts/test-generated-application.sh' qualification true
assert_scope 'scripts/test-apphost-launch-profile.sh' packaging true
assert_scope 'scripts/test-apphost-launch-profile.sh' qualification true
assert_scope 'scripts/test-apphost-launch-profile.sh' template true
assert_scope 'scripts/test-vercel-deployment.sh' web true
assert_scope 'scripts/test-vercel-deployment.sh' backend false
assert_scope 'scripts/test-vercel-deployment.sh' qualification true
assert_scope '.github/workflows/ci.yml' docs true
assert_scope '.github/workflows/ci.yml' backend true
assert_scope '.github/workflows/ci.yml' web true
assert_scope '.github/workflows/ci.yml' observability true
assert_scope '.github/workflows/ci.yml' deployment true
assert_scope '.github/workflows/ci.yml' packaging true
assert_scope '.github/workflows/ci.yml' qualification true
