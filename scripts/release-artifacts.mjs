import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageIds = ['Trykatch.Cli', 'Trykatch.Templates'];
export const qualificationGates = ['template-matrix', 'federation', 'blueprint-backend', 'blueprint-web', 'generated-production'];

function validateIdentity(version, commit) {
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/.test(version)) throw new Error('Invalid release version.');
  if (!/^[0-9a-f]{40}$/.test(commit)) throw new Error('Invalid release commit.');
}

function inspectPackages(directory, version) {
  const names = packageIds.map(id => `${id}.${version}.nupkg`);
  const actual = readdirSync(directory).sort();
  if (JSON.stringify(actual) !== JSON.stringify(names)) throw new Error('Expected exactly the two versioned release packages, without extra files.');
  return names.map(name => {
    const file = resolve(directory, name);
    const info = lstatSync(file);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('Release packages must be regular files.');
    return { name, bytes: info.size, sha256: createHash('sha256').update(readFileSync(file)).digest('hex') };
  });
}

export function createManifest(directory, version, commit, sourceCiRunId, releaseRunId) {
  validateIdentity(version, commit);
  if (!/^\d+$/.test(sourceCiRunId) || !/^\d+$/.test(releaseRunId)) throw new Error('Successful source CI and release run IDs are required.');
  return { schemaVersion: 1, state: 'candidate', version, commit, sourceCiRunId, releaseRunId, packages: inspectPackages(directory, version), gates: [] };
}

export function verifyManifest(directory, manifest, version, commit, releaseRunId, requireQualified = false, sourceCiRunId) {
  validateIdentity(version, commit);
  if (manifest.schemaVersion !== 1 || manifest.version !== version || manifest.commit !== commit ||
      !/^\d+$/.test(manifest.sourceCiRunId) || manifest.releaseRunId !== releaseRunId ||
      !['candidate', 'qualified'].includes(manifest.state)) throw new Error('Release manifest identity does not match this workflow run.');
  if (JSON.stringify(manifest.packages) !== JSON.stringify(inspectPackages(directory, version))) throw new Error('Release package digest/size mismatch.');
  if (manifest.state === 'qualified' && JSON.stringify(manifest.gates) !== JSON.stringify(qualificationGates)) throw new Error('Required package qualification gates are missing.');
  if (requireQualified && manifest.state !== 'qualified') throw new Error('Unqualified packages cannot be published.');
  if ((requireQualified || sourceCiRunId !== undefined) && manifest.sourceCiRunId !== sourceCiRunId) throw new Error('Manifest source CI does not match the approved successful run.');
  return manifest;
}

export function sealManifest(directory, manifest, version, commit, releaseRunId, passedGates) {
  verifyManifest(directory, manifest, version, commit, releaseRunId);
  if (JSON.stringify(passedGates) !== JSON.stringify(qualificationGates)) throw new Error('Every required qualification gate must pass before sealing.');
  return { ...manifest, state: 'qualified', gates: [...passedGates] };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [command, directory, manifestPath, version, commit, releaseRunId, ...arguments_] = process.argv.slice(2);
    let manifest;
    if (command === 'create') {
      manifest = createManifest(directory, version, commit, arguments_[0], releaseRunId);
    } else if (command === 'seal' || command === 'verify') {
      manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
      if (command === 'seal') manifest = sealManifest(directory, manifest, version, commit, releaseRunId, arguments_);
      else {
        const sourceCiIndex = arguments_.indexOf('--source-ci-run-id');
        verifyManifest(directory, manifest, version, commit, releaseRunId, arguments_.includes('--require-qualified'),
          sourceCiIndex === -1 ? undefined : arguments_[sourceCiIndex + 1]);
      }
    } else {
      throw new Error('Use create, seal or verify with directory, manifest, version, commit and workflow run ID.');
    }
    if (command !== 'verify') writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    console.log(`Release artifacts ${command}: ${version} at ${commit}.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
