#!/usr/bin/env bash
set -euo pipefail

script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
detector="$script_root/detect-changes.sh"
docs_contract="$script_root/check-docs-required.sh"

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
assert_scope 'templates/trykatch/.agents/skills/trykatch-build-module/SKILL.md' packaging true
assert_scope 'templates/trykatch/.agents/skills/trykatch-build-module/SKILL.md' docs true
assert_scope 'templates/trykatch/docs/development/backend.md' packaging true
assert_scope 'templates/trykatch/docs/ai-assisted-development.md' packaging true
assert_scope 'templates/trykatch/docs/developer-onboarding.md' packaging true
assert_scope 'templates/trykatch/docs/developer-onboarding.fr.md' docs true
assert_scope 'templates/trykatch/docs/first-feature.md' packaging true
assert_scope 'templates/trykatch/docs/first-feature.fr.md' docs true
assert_scope 'templates/trykatch/docs/backend-only-http.md' packaging true
assert_scope 'templates/trykatch/docs/backend-only-http.fr.md' docs true
assert_scope 'scripts/test-first-feature.sh' qualification true
assert_scope 'templates/trykatch/AGENTS.md' packaging true
assert_scope 'scripts/test-development-skills.mjs' packaging true
assert_scope 'scripts/test-development-skills.sh' packaging true
assert_scope 'templates/trykatch/src/API/Trykatch.Api/Program.cs' backend true
assert_scope 'templates/trykatch/src/API/Trykatch.Api/Program.cs' web true
assert_scope '.github/workflows/release.yml' packaging true
assert_scope 'scripts/lib/qualification-packages.sh' packaging true
assert_scope 'scripts/release-artifacts.mjs' qualification true
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
assert_scope 'templates/trykatch/tools/Trykatch.ModuleTool/ModuleScaffolder.cs' backend true
assert_scope 'templates/trykatch/tools/Trykatch.ModuleTool/ModuleScaffolder.cs' web true
assert_scope 'templates/trykatch/tools/Trykatch.ModuleTool/ModuleScaffolder.cs' packaging true
assert_scope 'templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/WebIndex.tsx.tpl' web true
assert_scope 'templates/trykatch/tools/Trykatch.ModuleTool/Program.cs' packaging true
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
assert_scope 'templates/trykatch/.dockerignore' packaging true
assert_scope 'templates/trykatch/.dockerignore' deployment true
assert_scope 'templates/trykatch/.dockerignore' qualification true
assert_scope 'templates/trykatch/deploy/observability/tempo.yml' qualification false
assert_scope 'scripts/test-generated-application.sh' qualification true
assert_scope 'scripts/test-apphost-launch-profile.sh' packaging true
assert_scope 'scripts/test-apphost-launch-profile.sh' qualification true
assert_scope 'scripts/test-apphost-launch-profile.sh' template true
assert_scope 'scripts/test-artifact-boundaries.sh' packaging true
assert_scope 'scripts/test-artifact-boundaries.sh' deployment true
assert_scope 'scripts/test-artifact-boundaries.sh' qualification true
assert_scope 'scripts/test-vercel-deployment.sh' web true
assert_scope 'scripts/test-vercel-deployment.sh' backend false
assert_scope 'scripts/test-vercel-deployment.sh' qualification true

printf '%s\n' \
  'templates/trykatch/tools/Trykatch.ModuleTool/Program.cs' \
  'docs-site/src/content/docs/modules/authoring.md' |
  bash "$docs_contract" --paths >/dev/null

if printf '%s\n' 'templates/trykatch/tools/Trykatch.ModuleTool/Program.cs' |
  bash "$docs_contract" --paths >/dev/null 2>&1; then
  printf 'Expected the documentation contract to reject an undocumented CLI change.\n' >&2
  exit 1
fi
assert_scope '.github/workflows/ci.yml' docs true
assert_scope '.github/workflows/ci.yml' backend true
assert_scope '.github/workflows/ci.yml' web true
assert_scope '.github/workflows/ci.yml' observability true
assert_scope '.github/workflows/ci.yml' deployment true
assert_scope '.github/workflows/ci.yml' packaging true
assert_scope '.github/workflows/ci.yml' qualification true
