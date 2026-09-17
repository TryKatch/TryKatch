import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const template = new URL('../../templates/trykatch/', import.meta.url);

test('generated SDK policy pins the tested feature band with patch roll-forward', () => {
  const generated = JSON.parse(readFileSync(new URL('global.json', template), 'utf8'));
  const source = JSON.parse(readFileSync(new URL('../../global.json', import.meta.url), 'utf8'));
  assert.equal(generated.sdk.version, source.sdk.version);
  assert.equal(generated.sdk.rollForward, 'latestPatch');
  assert.equal(source.sdk.rollForward, 'latestPatch');
  for (const name of ['ci.yml', 'release.yml']) {
    const workflow = readFileSync(new URL(`../../.github/workflows/${name}`, import.meta.url), 'utf8');
    for (const [, version] of workflow.matchAll(/dotnet-version:\s*(\S+)/g)) assert.equal(version, '10.0.3xx');
  }
});

test('every generated workflow action is pinned to a commit', () => {
  for (const name of ['ci.yml', 'web.yml', 'dependency-review.yml']) {
    const workflow = readFileSync(new URL(`.github/workflows/${name}`, template), 'utf8');
    const actions = [...workflow.matchAll(/uses:\s+([^\s#]+)/g)];
    assert.ok(actions.length > 0);
    for (const [, action] of actions) assert.match(action, /@[0-9a-f]{40}$/, `${name}: floating ${action}`);
  }
});

test('first generated restore does not enable NuGet caching on excluded lockfiles', () => {
  const definition = JSON.parse(readFileSync(new URL('.template.config/template.json', template), 'utf8'));
  assert.ok(definition.sources[0].exclude.includes('**/packages.lock.json'));
  for (const name of ['ci.yml', 'web.yml']) {
    const workflow = readFileSync(new URL(`.github/workflows/${name}`, template), 'utf8');
    const sdk = workflow.split('uses: actions/setup-dotnet@')[1].split(/\n\s+- (?:uses|run|name):/)[0];
    assert.doesNotMatch(sdk, /cache: true|cache-dependency-path/);
    assert.match(sdk, /dotnet-version: 10\.0\.3xx/);
  }
});

test('both source and generated web jobs build API before comparing generated contracts', () => {
  for (const url of [new URL('../../.github/workflows/ci.yml', import.meta.url), new URL('.github/workflows/web.yml', template)]) {
    const workflow = readFileSync(url, 'utf8').split('  verify-web:')[1];
    const build = workflow.indexOf('dotnet build');
    const generate = workflow.indexOf(' pnpm --dir ');
    assert.ok(build >= 0 && generate > build);
    assert.ok(workflow.indexOf('git diff --exit-code') > generate);
  }
});
