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

dotnet new trykatch -n Skill.FullStack -o "$skill_test_root/full-stack" --allow-scripts yes --debug:custom-hive "$skill_test_root/hive" | tee "$skill_test_root/full-stack-generation.log"
node "$repository_root/scripts/test-development-skills.mjs" "$skill_test_root/full-stack" Skill.FullStack
test -f "$skill_test_root/full-stack/web/package.json"
rg -q 'docs/developer-onboarding.md' "$skill_test_root/full-stack-generation.log"
rg -q 'docs/first-feature.md' "$skill_test_root/full-stack-generation.log"
rg -q 'backend/frontend wiring' "$skill_test_root/full-stack-generation.log"
rg -q 'No additional AI provider key' "$skill_test_root/full-stack-generation.log"

dotnet run --project "$repository_root/templates/trykatch/tools/Trykatch.ModuleTool" -- new Skill.Backend-Only --output "$skill_test_root/backend" --ui none --debug:custom-hive "$skill_test_root/hive" | tee "$skill_test_root/backend-generation.log"
node "$repository_root/scripts/test-development-skills.mjs" "$skill_test_root/backend" Skill.Backend.Only
test ! -e "$skill_test_root/backend/web"
rg -q 'Application generation' "$skill_test_root/backend-generation.log"
rg -q 'docs/developer-onboarding.md' "$skill_test_root/backend-generation.log"
rg -q 'docs/first-feature.md' "$skill_test_root/backend-generation.log"
rg -q 'skip frontend' "$skill_test_root/backend-generation.log"

no_scripts_exit=0
dotnet new trykatch -n Skill.NoScripts -o "$skill_test_root/no-scripts" --ui none --allow-scripts no --debug:custom-hive "$skill_test_root/hive" 2>&1 | tee "$skill_test_root/no-scripts-generation.log" || no_scripts_exit=${PIPESTATUS[0]}
# The existing Git script is refused by explicit operator choice: .NET returns
# post-action exit 105 after creating files. Onboarding must still print, without
# silently authorizing that script or treating an unexpected engine error as OK.
test "$no_scripts_exit" = 105
test ! -d "$skill_test_root/no-scripts/.git"
rg -q "Execution of 'Run script' post action is not allowed" "$skill_test_root/no-scripts-generation.log"
rg -q 'docs/developer-onboarding.md' "$skill_test_root/no-scripts-generation.log"
rg -q 'docs/first-feature.md' "$skill_test_root/no-scripts-generation.log"
rg -q 'No additional AI provider key' "$skill_test_root/no-scripts-generation.log"
node "$repository_root/scripts/test-development-skills.mjs" "$skill_test_root/no-scripts" Skill.NoScripts
printf 'Packed development skills passed for React and backend-only applications.\n'
