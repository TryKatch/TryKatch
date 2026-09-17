#!/usr/bin/env bash
set -euo pipefail

mode=${1:-backend}
case "$mode" in backend|web) ;; *) printf 'Use backend or web.\n' >&2; exit 2 ;; esac
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
feature_test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-first-feature.XXXXXX")
feature_test_root=$(cd "$feature_test_root" && pwd -P)
printf 'First-feature acceptance workspace (retained): %s\n' "$feature_test_root"
# No global template/tool install, secrets, AppHost startup or product database.
# Docker is needed for an isolated PostgreSQL Testcontainer. Runtime HTTP/browser
# and first-time-developer usability checks remain separate from this harness.
cli_version="0.1.0-ci.$(basename "$feature_test_root")"
dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$feature_test_root/packages"
packages=("$feature_test_root"/packages/Trykatch.Templates.*.nupkg)
test "${#packages[@]}" = 1
dotnet new install "${packages[0]}" --debug:custom-hive "$feature_test_root/hive"
if [[ $mode == backend ]]; then
  dotnet new trykatch -n FirstFeatureAcceptance -o "$feature_test_root/app" --ui none --allow-scripts yes \
    --debug:custom-hive "$feature_test_root/hive"
else
  dotnet new trykatch -n FirstFeatureAcceptance -o "$feature_test_root/app" --allow-scripts yes \
    --debug:custom-hive "$feature_test_root/hive"
fi
node "$repository_root/scripts/test-development-skills.mjs" "$feature_test_root/app" FirstFeatureAcceptance
dotnet pack "$repository_root/templates/trykatch/tools/Trykatch.ModuleTool" -c Release \
  -o "$feature_test_root/packages" -p:PackageVersion="$cli_version"
dotnet tool install Trykatch.Cli --version "$cli_version" --tool-path "$feature_test_root/tools" \
  --add-source "$feature_test_root/packages"
cd "$feature_test_root/app"
cli="$feature_test_root/tools/trykatch"
"$cli" doctor | tee "$feature_test_root/doctor.txt"
"$cli" setup | tee "$feature_test_root/setup.txt"
"$cli" status | tee "$feature_test_root/status.txt"
grep -Fq -- 'Runtime health: unknown' "$feature_test_root/status.txt"
create_args=(module create Equipment --entity EquipmentItem --resource equipment_items --ownership organization
  --fields 'name:string:required:max(120),dailyRate:decimal:required')
if [[ $mode == web ]]; then create_args+=(--with-web); fi
"$cli" "${create_args[@]}" \
  | tee "$feature_test_root/create.txt"
grep -Fq -- "Module 'equipment' created, registered, and enabled" "$feature_test_root/create.txt"
grep -Fq -- 'Running generated unit tests' "$feature_test_root/create.txt"
grep -Fq -- 'Running generated architecture tests' "$feature_test_root/create.txt"
"$cli" module facts equipment | tee "$feature_test_root/facts.txt"
"$cli" module doctor
node --input-type=module - "$mode" <<'JAVASCRIPT'
import assert from 'node:assert/strict';
import { readFile, stat } from 'node:fs/promises';
const mode = process.argv[2];
const read = relative => readFile(relative, 'utf8');
const manifest = JSON.parse(await read('src/Modules/Equipment/trykatch.module.json'));
assert.equal(manifest.id, 'equipment');
assert.equal(manifest.dataOwnership.default, 'organization');
assert.equal(manifest.dataOwnership.resources[0].table, 'equipment_items');
assert.deepEqual(manifest.contributions.permissions, ['equipment.read', 'equipment.manage']);
assert.equal(manifest.capabilities.includes('web'), mode === 'web');
assert.match(await read('src/API/FirstFeatureAcceptance.Api/Modules/EnabledModules.cs'), /new EquipmentModule\(\)/);
const module = await read('src/Modules/Equipment/FirstFeatureAcceptance.Modules.Equipment.Infrastructure/EquipmentModule.cs');
assert.match(module, /"Name" character varying\(120\) NOT NULL/);
assert.match(module, /"DailyRate" numeric\(18, 2\) NOT NULL/);
assert.match(module, /FORCE ROW LEVEL SECURITY/);
const endpoints = await read('src/Modules/Equipment/FirstFeatureAcceptance.Modules.Equipment.Presentation/EquipmentEndpoints.cs');
assert.match(endpoints, /string\? DailyRate/);
assert.match(endpoints, /permission:equipment\.read/);
assert.match(endpoints, /permission:equipment\.manage/);
if (mode === 'web') {
  assert.equal(manifest.contributions.routes[0].path, '/equipment_items');
  const messages = await read('src/Modules/Equipment/Web/src/messages.ts');
  assert.match(messages, /en: \{/);
  assert.match(messages, /fr: \{/);
  assert.match(messages, /fieldDailyRate:/);
} else {
  assert.equal(await stat('web').catch(() => null), null);
}
JAVASCRIPT
# Make missing/misnamed tests a failure, not a green zero-test run.
dotnet test tests/FirstFeatureAcceptance.IntegrationTests \
  --filter FullyQualifiedName~EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole \
  --logger trx --results-directory "$feature_test_root/results"
node --input-type=module - "$feature_test_root/results" <<'JAVASCRIPT'
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
const directory = process.argv[2];
const reports = (await readdir(directory)).filter(name => name.endsWith('.trx'));
assert.equal(reports.length, 1, 'Expected one real PostgreSQL test report');
const report = await readFile(path.join(directory, reports[0]), 'utf8');
assert.match(report, /testName="EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole"[^>]*outcome="Passed"/);
assert.match(report, /<Counters\b[^>]*total="1"[^>]*executed="1"[^>]*passed="1"/);
JAVASCRIPT
if [[ $mode == web ]]; then
  grep -Fq -- 'Installing frontend dependencies' "$feature_test_root/create.txt"
  grep -Fq -- 'Building the frontend' "$feature_test_root/create.txt"
  corepack pnpm --dir web generate:check
else
  test ! -e web
fi
printf 'Fresh %s application: startup configuration, Equipment generation and real PostgreSQL relation isolation passed.\n' "$mode"
printf 'Live AppHost/HTTP/browser and first-time-developer usability were NOT tested by this harness.\n'
