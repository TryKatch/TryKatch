#!/usr/bin/env bash
set -euo pipefail

docs=false
backend=false
web=false
observability=false
deployment=false
packaging=false
qualification=false

mark_all() {
  docs=true
  backend=true
  web=true
  observability=true
  deployment=true
  packaging=true
  qualification=true
}

classify_path() {
  local path=$1

  case "$path" in
    .github/workflows/ci.yml|scripts/ci/*|global.json)
      mark_all
      ;;
    docs-site/*|docs/*|README.md|CONTRIBUTING.md|SECURITY.md|templates/trykatch/docs/*|templates/trykatch/README*.md|templates/trykatch/AGENTS.md|templates/trykatch/CONTEXT.md)
      docs=true
      ;;
    Trykatch.Templates.csproj|scripts/test-template.sh|templates/trykatch/.template.config/*|templates/trykatch/.github/*|templates/trykatch/LICENSE)
      packaging=true
      ;;
    scripts/test-generated-application.sh)
      backend=true
      web=true
      deployment=true
      packaging=true
      qualification=true
      ;;
    scripts/test-observability.sh|templates/trykatch/scripts/test-observability.sh|templates/trykatch/deploy/observability/*)
      observability=true
      ;;
    templates/trykatch/deploy/postgres/*)
      deployment=true
      ;;
    scripts/test-migrator.sh)
      deployment=true
      ;;
    templates/trykatch/compose*.yml|templates/trykatch/.env.example)
      deployment=true
      observability=true
      ;;
    scripts/test-federation-module.sh|templates/trykatch/trykatch.modules*.json|templates/trykatch/schemas/*)
      backend=true
      web=true
      packaging=true
      ;;
    templates/trykatch/src/*|templates/trykatch/tests/*|templates/trykatch/tools/TrykatchApp.ModuleTool/*|templates/trykatch/Directory.*|templates/trykatch/TrykatchApp.slnx|templates/trykatch/dotnet-tools.json)
      backend=true
      ;;
    templates/trykatch/web/*|templates/trykatch/tools/*.mjs)
      web=true
      ;;
    templates/trykatch/.dockerignore)
      backend=true
      web=true
      ;;
  esac
}

if [[ ${1:-} == --paths ]]; then
  while IFS= read -r path; do
    [[ -n $path ]] && classify_path "$path"
  done
else
  base_ref=${1:?Usage: detect-changes.sh <base-ref> [head-ref] or detect-changes.sh --paths}
  head_ref=${2:-HEAD}

  if [[ $base_ref =~ ^0+$ ]] || ! git cat-file -e "$base_ref^{commit}" 2>/dev/null; then
    mark_all
  else
    while IFS= read -r path; do
      [[ -n $path ]] && classify_path "$path"
    done < <(git diff --name-only --diff-filter=ACDMRT "$base_ref" "$head_ref")
  fi
fi

if [[ $backend == true || $web == true || $observability == true || $deployment == true || $packaging == true ]]; then
  template=true
else
  template=false
fi

if [[ $backend == true || $web == true || $deployment == true || $packaging == true ]]; then
  qualification=true
fi

printf 'docs=%s\n' "$docs"
printf 'backend=%s\n' "$backend"
printf 'web=%s\n' "$web"
printf 'observability=%s\n' "$observability"
printf 'deployment=%s\n' "$deployment"
printf 'packaging=%s\n' "$packaging"
printf 'qualification=%s\n' "$qualification"
printf 'template=%s\n' "$template"
