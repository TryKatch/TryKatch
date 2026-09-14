#!/usr/bin/env bash
set -euo pipefail

requires_docs=false
updates_docs=false

classify_path() {
  local path=$1

  case "$path" in
    Trykatch.Templates.csproj|RELEASE_VERSION|templates/trykatch/.template.config/*|templates/trykatch/tools/Trykatch.ModuleTool/*)
      requires_docs=true
      ;;
  esac

  case "$path" in
    README.md|docs/*|docs-site/src/content/docs/*|templates/trykatch/docs/*|templates/trykatch/README*.md)
      updates_docs=true
      ;;
  esac
}

if [[ ${1:-} == --paths ]]; then
  while IFS= read -r path; do
    [[ -n $path ]] && classify_path "$path"
  done
else
  base_ref=${1:?Usage: check-docs-required.sh <base-ref> [head-ref] or check-docs-required.sh --paths}
  head_ref=${2:-HEAD}

  if [[ $base_ref =~ ^0+$ ]] || ! git cat-file -e "$base_ref^{commit}" 2>/dev/null; then
    # A first push has no trustworthy comparison point; the release-version
    # contract still verifies all required documentation surfaces.
    exit 0
  fi

  while IFS= read -r path; do
    [[ -n $path ]] && classify_path "$path"
  done < <(git diff --name-only --diff-filter=ACDMRT "$base_ref" "$head_ref")
fi

if [[ $requires_docs == true && $updates_docs != true ]]; then
  printf '%s\n' \
    'Documentation contract failed: user-facing CLI or template changes require a matching documentation update.' >&2
  exit 1
fi

printf 'Documentation contract passed (required=%s, updated=%s).\n' "$requires_docs" "$updates_docs"
