import assert from 'node:assert/strict'
import { readdir, readFile, stat } from 'node:fs/promises'
import path from 'node:path'

const [applicationRoot, ...options] = process.argv.slice(2)
if (!applicationRoot || options.some(option => option !== '--openapi')) {
  throw new Error('Usage: node scripts/assert-minimal-profile.mjs <application-root> [--openapi]')
}

const root = path.resolve(applicationRoot)
const read = relative => readFile(path.join(root, relative), 'utf8')
const apiProjects = (await readdir(path.join(root, 'src/API'))).filter(name => name.endsWith('.Api'))
assert.equal(apiProjects.length, 1, 'Expected one generated API project')
const applicationName = apiProjects[0].slice(0, -'.Api'.length)
const catalog = JSON.parse(await read('trykatch.modules.json'))
const lock = JSON.parse(await read('trykatch.modules.lock.json'))
const expectedExamples = ['documents', 'federation', 'projects']

assert.deepEqual(
  catalog.modules.map(module => module.id).sort(),
  expectedExamples,
  'Minimal profile must keep the reference module registrations available',
)
assert.ok(catalog.modules.every(module => module.enabled === false), 'Minimal profile must disable every installed module')
assert.ok(lock.modules.every(module => module.enabled === false), 'Minimal profile module lock must match the disabled catalog')

for (const relative of [
  `src/API/${applicationName}.Api/Modules/EnabledModules.cs`,
  `src/API/${applicationName}.Migrator/Modules/EnabledModules.cs`,
]) {
  const registry = await read(relative)
  assert.match(registry, /IReadOnlyList<IModule> All\s*\{\s*get;\s*\}\s*=\s*\[\s*\]/s, `${relative} must have no enabled modules`)
  assert.doesNotMatch(registry, /ProjectsModule|DocumentsModule|FederationModule/)
}

const webRegistry = await read('web/apps/web/src/modules.ts')
assert.doesNotMatch(webRegistry, /@trykatch-modules\/(projects|documents|federation)/)
assert.match(webRegistry, /new WebModuleCatalog\(\[\s*\], workspaceOverrides, EXPECTED_FEATURES\)/)

for (const example of ['Projects', 'Documents', 'Federation']) {
  assert.ok(await stat(path.join(root, `src/Modules/${example}/trykatch.module.json`)), `${example} source must remain available`)
}
assert.ok(await stat(path.join(root, 'src/Modules/Documents/README.md')), 'Documents migration guidance must remain available')

if (options.includes('--openapi')) {
  const openApi = JSON.parse(await read(`web/packages/api-client/openapi/${applicationName}.Api.json`))
  const examplePaths = Object.keys(openApi.paths).filter(route => /\/api\/v1\/(projects|documents)(\/|$)/.test(route))
  assert.deepEqual(examplePaths, [], 'Minimal profile OpenAPI must omit disabled example module endpoints')
}

console.log('Minimal module profile registries and retained reference source are valid.')
