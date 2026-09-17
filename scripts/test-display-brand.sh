#!/usr/bin/env bash
set -euo pipefail

# Run against the actual packed template, without altering the user's template hive.
package_path=${1:?Usage: bash scripts/test-display-brand.sh /absolute/path/to/template.nupkg}
brand_test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-display-brand.XXXXXX")
brand_test_root=$(cd "$brand_test_root" && pwd -P)
trap 'rm -rf "$brand_test_root"' EXIT
dotnet new install "$package_path" --debug:custom-hive "$brand_test_root/hive" --force
dotnet new trykatch -n Baseline.Technical --ui none --email true -o "$brand_test_root/default" --debug:custom-hive "$brand_test_root/hive" --allow-scripts yes
dotnet new trykatch -n Custom.Technical --display-name 'Kamenta & "partners"' --email true -o "$brand_test_root/custom" --debug:custom-hive "$brand_test_root/hive" --allow-scripts yes
node - "$brand_test_root" <<'NODE'
const fs = require('node:fs');
const assert = require('node:assert/strict');
const root = process.argv[2];
const read = (path) => fs.readFileSync(`${root}/${path}`, 'utf8');
assert.match(read('default/src/Common/Baseline.Technical.Infrastructure/Optional/Email/EmailBranding.cs'), /ApplicationName.*= "Trykatch";/);
assert(!fs.existsSync(`${root}/default/web`));
const name = 'Kamenta & "partners"';
const literal = JSON.stringify(name);
assert(read('custom/src/Common/Custom.Technical.Infrastructure/Optional/Email/EmailBranding.cs').includes(`= ${literal};`));
assert(read('custom/web/apps/web/src/branding.ts').includes(`|| ${literal}`));
assert.match(read('custom/src/API/Custom.Technical.Api/Program.cs'), /using Custom.Technical.Api;/);
for (const path of ['views/LoginPage.tsx', 'views/ForgotPasswordPage.tsx', 'views/ResetPasswordPage.tsx', 'views/Pages.tsx', 'views/LandingPage.tsx', 'shell/AppShell.tsx', 'shell/PlatformShell.tsx']) {
  const source = read(`custom/web/apps/web/src/${path}`);
  assert(source.includes('import { applicationName }'));
  assert(!source.includes('<strong>Custom.Technical</strong>'));
  assert(!source.includes('sidebar-label">Custom.Technical<'));
}
assert(!read('custom/src/API/Custom.Technical.AppHost/ApplicationHostingExtensions.cs').includes('Email__From'));
assert(!read('custom/src/API/Custom.Technical.Api/appsettings.Development.json').includes('"From"'));
console.log('Display-brand package contract: PASS (default, custom, quoted name, namespaces, UI and sender wiring).');
NODE
