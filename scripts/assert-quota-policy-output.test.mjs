import assert from 'node:assert/strict'
import { copyFile, mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { spawnSync } from 'node:child_process'
import test from 'node:test'
import { requiredTestCases, requiredTestCaseCount } from './assert-quota-policy-output.mjs'

const scriptPath = fileURLToPath(new URL('./assert-quota-policy-output.mjs', import.meta.url))
const repositoryRoot = path.resolve(path.dirname(scriptPath), '..')
const templateRoot = path.join(repositoryRoot, 'templates/trykatch')
const caseCounts = new Map(requiredTestCases.map(name => [name, 1]))
caseCounts.set('QuotaUsageHttpAllowsEachOrganizationPermissionAndSerializesInt64AsDecimalString', 2)
caseCounts.set('IneligibleOrganizationPausesMaintenanceAndResumesWhenEligible', 2)
caseCounts.set('KilledUploadProcessRetainsOnlyItsCommittedEffectsAndFreshWorkerRecovers', 3)
const requiredCases = [...caseCounts]
const expectedTotal = requiredTestCaseCount

function expectTrx(cases = requiredCases, { total = expectedTotal, passed = expectedTotal, notExecuted = 0 } = {}) {
  const results = cases.flatMap(([name, count]) => Array.from({ length: count }, (_, index) =>
    `<UnitTestResult testName="${name}${count > 1 ? ` (${index + 1})` : ''}" outcome="Passed" />`)).join('')
  return `<TestRun><ResultSummary><Counters total="${total}" executed="${total - notExecuted}" passed="${passed}" failed="${total - passed - notExecuted}" notExecuted="${notExecuted}" /></ResultSummary><Results>${results}</Results></TestRun>`
}

async function fixture({ mutateOpenApi, cases, counters, backendOnly = false } = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'trykatch-quota-profile-'))
  const generatedRoot = path.join(root, 'generated')
  const openApiDirectory = backendOnly
    ? path.join(generatedRoot, 'src/API/Horizon.Api/obj/openapi')
    : path.join(generatedRoot, 'web/packages/api-client/openapi')
  await mkdir(openApiDirectory, { recursive: true })
  await mkdir(path.join(generatedRoot, 'docs/generated'), { recursive: true })
  await mkdir(path.join(generatedRoot, 'tools'), { recursive: true })
  if (!backendOnly) await writeFile(path.join(generatedRoot, 'web/package.json'), '{}')
  const canonical = JSON.parse(await readFile(path.join(templateRoot, 'web/packages/api-client/openapi/Trykatch.Api.json'), 'utf8'))
  const actual = structuredClone(canonical)
  mutateOpenApi?.(actual)
  await writeFile(path.join(openApiDirectory, 'Horizon.Api.json'), JSON.stringify(actual))
  const manifest = JSON.parse(await readFile(path.join(templateRoot, 'src/Modules/Documents/trykatch.module.json'), 'utf8'))
  const facts = {
    modules: [{
      id: 'documents',
      version: manifest.version,
      enabled: true,
      quotaResources: manifest.quotaResources.map(resource => ({
        key: resource.key,
        name: resource.name,
        description: resource.description,
        unit: resource.unit,
        uploadPolicy: resource.uploadPolicy ?? null,
        inventorySource: resource.inventorySource ?? null,
        removalPolicy: resource.removalPolicy ?? null,
      })),
    }],
  }
  await writeFile(path.join(root, 'facts.json'), JSON.stringify(facts))
  const assistantGeneratorPath = path.join(generatedRoot, 'tools/generate-assistant-contract.mjs')
  await copyFile(path.join(templateRoot, 'tools/generate-assistant-contract.mjs'), assistantGeneratorPath)
  const assistantModule = await import(pathToFileURL(assistantGeneratorPath))
  await assistantModule.generate({
    openApiPath: path.join(openApiDirectory, 'Horizon.Api.json'),
    outputPath: path.join(generatedRoot, 'docs/generated/assistant-contract.json'),
  })
  const trx = expectTrx(cases, counters)
  await writeFile(path.join(root, 'results.trx'), trx)
  return { root, generatedRoot, factsPath: path.join(root, 'facts.json'), trxPath: path.join(root, 'results.trx') }
}

async function run(args = {}) {
  const current = await fixture(args)
  try {
    const result = spawnSync(process.execPath, [scriptPath, current.generatedRoot, 'Horizon', current.factsPath, current.trxPath], { encoding: 'utf8' })
    return { ...result, status: result.status, output: `${result.stdout ?? ''}${result.stderr ?? ''}` }
  } finally {
    await rm(current.root, { recursive: true, force: true })
  }
}

test('quota output verifier accepts matching generated products and complete controlled-report evidence', async () => {
  const result = await run()
  assert.equal(result.status, 0, result.output)
})

test('quota output verifier rejects enum-valued units in otherwise matching module facts', async () => {
  const current = await fixture()
  try {
    const facts = JSON.parse(await readFile(current.factsPath, 'utf8'))
    facts.modules.find(module => module.id === 'documents').quotaResources[0].unit = 1
    await writeFile(current.factsPath, JSON.stringify(facts))
    const result = spawnSync(process.execPath, [scriptPath, current.generatedRoot, 'Horizon', current.factsPath, current.trxPath], { encoding: 'utf8' })
    assert.notEqual(result.status, 0)
    assert.match(`${result.stdout ?? ''}${result.stderr ?? ''}`, /Generated installed Documents quota declarations drifted/)
  } finally {
    await rm(current.root, { recursive: true, force: true })
  }
})

test('quota output verifier reads the generated backend-only OpenAPI location', async () => {
  const current = await fixture({ backendOnly: true })
  try {
    const result = spawnSync(process.execPath, [scriptPath, current.generatedRoot, 'Horizon', current.factsPath, current.trxPath], { encoding: 'utf8' })
    assert.equal(result.status, 0, `${result.stdout ?? ''}${result.stderr ?? ''}`)
  } finally {
    await rm(current.root, { recursive: true, force: true })
  }
})

test('quota output verifier rejects generated OpenAPI or schema drift', async () => {
  const result = await run({ mutateOpenApi: openApi => { openApi.components.schemas.OrganizationQuotaUsageResource.properties.committed.type = 'integer' } })
  assert.notEqual(result.status, 0)
})

test('quota output verifier rejects zero, skipped, or failed selected cases', async () => {
  const zero = await run({ cases: [], counters: { total: 0, passed: 0, notExecuted: 0 } })
  const skipped = await run({ counters: { total: expectedTotal, passed: expectedTotal - 1, notExecuted: 1 } })
  assert.notEqual(zero.status, 0)
  assert.notEqual(skipped.status, 0)
})

test('quota output verifier rejects missing required quota or acceptance methods', async () => {
  const result = await run({ cases: requiredCases.filter(([name]) => name !== requiredTestCases[0]) })
  assert.notEqual(result.status, 0)
})
