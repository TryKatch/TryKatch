#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
skill_test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-skills.XXXXXX")
skill_test_root=$(cd "$skill_test_root" && pwd -P)
printf 'Development skills acceptance workspace: %s\n' "$skill_test_root"
cleanup() {
  local result=$?
  if [[ $result == 0 && ${TRYKATCH_KEEP_SKILL_WORKSPACE:-false} != true ]]; then
    rm -rf "$skill_test_root"
  else
    printf 'Skills workspace retained: %s\n' "$skill_test_root"
  fi
}
trap cleanup EXIT

dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$skill_test_root/packages"
packages=("$skill_test_root"/packages/Trykatch.Templates.*.nupkg)
test "${#packages[@]}" = 1
dotnet new install "${packages[0]}" --debug:custom-hive "$skill_test_root/hive"

dotnet new trykatch -n Skill.FullStack -o "$skill_test_root/full-stack" --allow-scripts yes --debug:custom-hive "$skill_test_root/hive"
node "$repository_root/scripts/test-development-skills.mjs" "$skill_test_root/full-stack" Skill.FullStack
test -f "$skill_test_root/full-stack/web/package.json"

dotnet new trykatch -n Skill.Backend-Only -o "$skill_test_root/backend" --ui none --allow-scripts yes --debug:custom-hive "$skill_test_root/hive"
node "$repository_root/scripts/test-development-skills.mjs" "$skill_test_root/backend" Skill.Backend.Only
test ! -e "$skill_test_root/backend/web"
printf 'Packed development skills passed for React and backend-only applications.\n'
