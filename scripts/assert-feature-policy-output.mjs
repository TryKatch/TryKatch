import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const [generatedRoot, namespace, factsFile, reportFile] = process.argv.slice(2)
assert.ok(generatedRoot && namespace && factsFile, 'Expected generated root, namespace and actual module facts file.')
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const templateRoot = path.join(repositoryRoot, 'templates/trykatch')
const readJson = async (file) => JSON.parse(await readFile(file, 'utf8'))
const canonical = await readJson(path.join(templateRoot, 'web/packages/api-client/openapi/Trykatch.Api.json'))
const hasWeb = await readFile(path.join(generatedRoot, 'web/package.json')).then(() => true, () => false)
const generatedOpenApi = path.join(generatedRoot, hasWeb
  ? `web/packages/api-client/openapi/${namespace}.Api.json`
  : `src/API/${namespace}.Api/obj/openapi/${namespace}.Api.json`)
const actual = await readJson(generatedOpenApi)
const methods = new Set(['get', 'post', 'put', 'patch', 'delete'])

function operations(document) {
  return Object.fromEntries(Object.entries(document.paths).flatMap(([route, item]) =>
    Object.entries(item).filter(([method, value]) => methods.has(method) && value.operationId)
      .map(([method, value]) => [`${method.toUpperCase()} ${route}`, {
        operationId: value.operationId,
        parameters: value.parameters,
        requestBody: value.requestBody,
        responses: value.responses,
        security: value.security,
      }])))
}

const expectedOperations = operations(canonical)
const actualOperations = operations(actual)
assert.deepEqual(Object.keys(actualOperations).sort(), Object.keys(expectedOperations).sort(), 'Generated public operation set drifted.')
for (const [operation, expected] of Object.entries(expectedOperations))
  assert.deepEqual(actualOperations[operation], expected, `Generated contract drift: ${operation}`)
assert.deepEqual(actual.components.schemas, canonical.components.schemas, 'Generated schemas drifted.')

const featureOperationIds = Object.values(actualOperations).map(value => value.operationId)
  .filter(value => value === 'Features_Current' || value.startsWith('PlatformFeatures_')).sort()
assert.deepEqual(featureOperationIds, [
  'Features_Current', 'PlatformFeatures_History', 'PlatformFeatures_List',
  'PlatformFeatures_SaveEnvironment', 'PlatformFeatures_SaveOrganization',
])
const projectsOperations = Object.entries(actualOperations).filter(([key]) => key.includes('/api/v1/projects'))
assert.equal(projectsOperations.length, 7, 'Expected the seven guarded Projects operations.')
for (const [operation, value] of projectsOperations) {
  assert.ok(value.responses['409'] && value.responses['503'], `Missing feature failure responses: ${operation}`)
}

const facts = await readJson(factsFile)
const projectFacts = facts.modules.find(module => module.id === 'projects')
const projectManifest = await readJson(path.join(templateRoot, 'src/Modules/Projects/trykatch.module.json'))
assert.ok(projectFacts?.enabled, 'Projects must be installed and enabled in both generated profiles.')
assert.equal(projectFacts.version, projectManifest.version)
assert.deepEqual(projectFacts.features, projectManifest.features, 'Generated installed feature declarations drifted.')
assert.deepEqual(actual['x-trykatch-modules'].map(({ id, version }) => ({ id, version })),
  canonical['x-trykatch-modules'].map(({ id, version }) => ({ id, version })), 'Generated installed API metadata drifted.')

const { buildAssistantContract } = await import(pathToFileURL(path.join(generatedRoot, 'tools/generate-assistant-contract.mjs')))
const expectedAssistant = buildAssistantContract(canonical)
const actualAssistant = buildAssistantContract(actual)
assert.deepEqual(actualAssistant.tools, expectedAssistant.tools, 'Generated assistant tool contracts drifted.')
assert.deepEqual(actualAssistant.bindings, expectedAssistant.bindings, 'Generated assistant bindings drifted.')
assert.deepEqual(actualAssistant.policy, expectedAssistant.policy)

if (reportFile) {
  const report = await readFile(reportFile, 'utf8')
  const counters = report.match(/<Counters\b([^>]*)/)
  assert.ok(counters, 'Expected real PostgreSQL test result counters.')
  const counts = Object.fromEntries([...counters[1].matchAll(/(\w+)="(\d+)"/g)].map(([, key, value]) => [key, Number(value)]))
  assert.ok(counts.total >= 39, 'The complete feature-policy selection must execute, including partial-class cases.')
  assert.equal(counts.executed, counts.total)
  assert.equal(counts.passed, counts.total, 'Every selected case must pass; skips are not qualification.')
  for (const name of [
    'FeatureAdministrationRejectsCurrentSuspendedUnconfirmedRevokedAndLegacyOnlyAuthority',
    'FeatureBusyLockAndAuthorityRevocationAtAdmissionLeaveNoControlsOrHistory',
    'FeatureSnapshotSurvivesDistributedCacheFailureButReturns503ForAuthoritativeStoreFailure',
    'PlatformFeatureAdministrationUsesDefaultAndExplicitIndependentPermissions',
    'PlatformFeatureSaveRequiresCsrfAndRollsBackControlWhenHistoryInsertFails',
    'ProjectsAssistantConversationCannotRepeatCachedReadAfterFeatureDisable',
    'ProjectsAssistantReadMapsFeatureUnavailableAndPolicyFailureThenRecovers',
    'PersistedProjectsCreateWorkCanRetryAfterWritesFeatureIsEnabled',
    'ProjectsReadFeatureFiltersHistoryAndRevokesLiveSubscriptionsWithoutStoppingPublication',
  ]) assert.ok(report.includes(`testName="${name}"`), `Missing required partial-class acceptance case: ${name}`)
  console.log(`${namespace}: all ${counts.passed} selected PostgreSQL cases passed with zero skips.`)
}
console.log(`${namespace}: actual OpenAPI, schemas, installed feature facts and assistant contracts match (${hasWeb ? 'React' : 'backend-only'}).`)
