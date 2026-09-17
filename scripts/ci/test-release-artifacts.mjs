import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { createManifest, qualificationGates, sealManifest, verifyManifest } from '../release-artifacts.mjs';

const version = '0.1.0-preview.26';
const commit = 'a'.repeat(40);
const runId = '456';

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'trykatch-release-fixture-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const directory = join(root, 'packages');
  mkdirSync(directory);
  for (const id of ['Trykatch.Cli', 'Trykatch.Templates']) writeFileSync(join(directory, `${id}.${version}.nupkg`), `harmless ${id} fixture`);
  return { root, directory, manifest: createManifest(directory, version, commit, '123', runId) };
}

test('qualified bytes verify after download into a different directory', t => {
  const { root, directory, manifest } = fixture(t);
  const sealed = sealManifest(directory, manifest, version, commit, runId, qualificationGates);
  const downloaded = join(root, 'downloaded');
  cpSync(directory, downloaded, { recursive: true });
  verifyManifest(downloaded, JSON.parse(JSON.stringify(sealed)), version, commit, runId, true, '123');
  assert.throws(() => verifyManifest(downloaded, sealed, version, commit, runId, true, '789'), /source CI/);
});

test('publication rejects candidates and failed or missing gates', t => {
  const { directory, manifest } = fixture(t);
  assert.throws(() => verifyManifest(directory, manifest, version, commit, runId, true), /Unqualified/);
  assert.throws(() => sealManifest(directory, manifest, version, commit, runId, qualificationGates.slice(1)), /Every required/);
  assert.throws(() => verifyManifest(directory, { ...manifest, state: 'qualified', gates: [] }, version, commit, runId, true), /gates are missing/);
});

test('altered bytes or rebuilt packages cannot pass the recorded digest', t => {
  const { directory, manifest } = fixture(t);
  writeFileSync(join(directory, `Trykatch.Cli.${version}.nupkg`), 'tampered package');
  assert.throws(() => verifyManifest(directory, manifest, version, commit, runId), /digest\/size mismatch/);
});

test('wrong commit, version, workflow run or schema cannot be published', t => {
  const { directory, manifest } = fixture(t);
  for (const changes of [{ commit: 'b'.repeat(40) }, { version: '1.0.0' }, { releaseRunId: '789' }, { schemaVersion: 2 }]) {
    assert.throws(() => verifyManifest(directory, { ...manifest, ...changes }, version, commit, runId), /identity/);
  }
});

test('extra and missing packages fail closed', t => {
  const { directory, manifest } = fixture(t);
  const extra = join(directory, 'unexpected.nupkg');
  writeFileSync(extra, 'extra');
  assert.throws(() => verifyManifest(directory, manifest, version, commit, runId), /exactly/);
  rmSync(extra);
  rmSync(join(directory, `Trykatch.Cli.${version}.nupkg`));
  assert.throws(() => verifyManifest(directory, manifest, version, commit, runId), /exactly/);
});

test('symlinked packages and malformed release identities are rejected', t => {
  const { root, directory, manifest } = fixture(t);
  const cli = join(directory, `Trykatch.Cli.${version}.nupkg`);
  rmSync(cli);
  const target = join(root, 'elsewhere');
  writeFileSync(target, 'fixture');
  symlinkSync(target, cli);
  assert.throws(() => verifyManifest(directory, manifest, version, commit, runId), /regular files/);
  assert.throws(() => createManifest(directory, '../bad', commit, '123', runId), /version/);
  assert.throws(() => createManifest(directory, version, 'invalid', '123', runId), /commit/);
});

test('prepared candidate selection never repacks either package', t => {
  const { root, directory, manifest } = fixture(t);
  const manifestPath = join(root, 'qualification.json');
  const checkout = process.cwd();
  const actualCommit = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: checkout, encoding: 'utf8' }).stdout.trim();
  writeFileSync(manifestPath, JSON.stringify({ ...manifest, commit: actualCommit }));
  const result = spawnSync('bash', ['-eu', '-c', `
    repository_root=$PWD
    dotnet() { echo 'Unexpected repack' >&2; return 99; }
    source scripts/lib/qualification-packages.sh
    prepare_qualification_packages "$TEST_OUTPUT"
    test "$qualification_template_package" = "$TRYKATCH_QUALIFICATION_PACKAGES/Trykatch.Templates.$TRYKATCH_QUALIFICATION_VERSION.nupkg"
  `], { cwd: checkout, encoding: 'utf8', env: { ...process.env, TEST_OUTPUT: join(root, 'unused'), TRYKATCH_QUALIFICATION_PACKAGES: realpathSync(directory),
    TRYKATCH_QUALIFICATION_MANIFEST: manifestPath, TRYKATCH_QUALIFICATION_VERSION: version, TRYKATCH_QUALIFICATION_RUN_ID: runId } });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
});

test('workflow keeps publication dependent on verified downloads and preserves preview identity', () => {
  const workflow = readFileSync(new URL('../../.github/workflows/release.yml', import.meta.url), 'utf8');
  for (const job of ['publish-preview', 'publish-stable']) {
    const section = workflow.split(`  ${job}:\n`)[1]?.split(/^  [a-z][a-z-]+:\n/m)[0];
    assert.ok(section, `Missing ${job} job`);
    assert.match(section, /needs: qualify/);
    assert.match(section, /actions\/download-artifact@[0-9a-f]{40}/);
    assert.match(section, /--require-qualified --source-ci-run-id/);
    assert.ok(section.indexOf('--require-qualified') < section.indexOf('NuGet/login@'));
    assert.doesNotMatch(section, /dotnet (?:pack|build)|skip-duplicate/);
    if (job === 'publish-preview') assert.doesNotMatch(section, /environment:/);
    else assert.match(section, /environment: stable-release/);
  }
  assert.match(workflow, /branches\/main.*protected/);
  assert.match(workflow, /required_reviewers/);
  assert.match(workflow, /Remaining hardening gates prohibit stable publication/);
});
