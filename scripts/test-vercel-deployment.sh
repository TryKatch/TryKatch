#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
template_root="$repository_root/templates/trykatch"
config="$template_root/vercel.json"
ignore_file="$template_root/.vercelignore"

fail() {
  printf 'Vercel deployment contract failed: %s\n' "$1" >&2
  exit 1
}

test -f "$config" || fail 'vercel.json must live at the template root so module packages are included'
test ! -e "$template_root/web/vercel.json" || fail 'web/vercel.json narrows the upload context and excludes module packages'
test -f "$ignore_file" || fail '.vercelignore must exclude local build artifacts from deployments'

for pattern in '**/bin/' '**/obj/' '**/node_modules/' 'tests/'; do
  grep -Fxq "$pattern" "$ignore_file" || fail ".vercelignore is missing $pattern"
done

jq -e '
  .installCommand == "corepack pnpm@10.17.1 --dir web install --frozen-lockfile" and
  .buildCommand == "corepack pnpm@10.17.1 --dir web --filter @trykatch/web build" and
  .outputDirectory == "web/apps/web/dist"
' "$config" >/dev/null || fail 'vercel.json does not build the web workspace from the template root'

for module in Projects Documents; do
  test -f "$template_root/src/Modules/$module/Web/package.json" ||
    fail "$module web package is outside the deployment context"
done
