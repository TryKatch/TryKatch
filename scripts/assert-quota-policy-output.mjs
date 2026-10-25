import assert from 'node:assert/strict'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

export const requiredTestCases = [
  'QuotaUsageInspectionAllowsEitherReadOrManageAndPreservesInt64AndOrganizationIsolation',
  'QuotaUsageInspectionDeniesMissingPermissionBeforePublishingOrQueuing',
  'QuotaUsageInspectionRechecksMembershipAndActualOrganizationFrame',
  'QuotaUsageInspectionInitializesDisabledInventoryWithoutPretendingPartialUsageIsComplete',
  'QuotaUsageInspectionSeparatesLiveAndExpiredHoldsWithoutRefundingEither',
  'QuotaUsageInspectionClassifiesFencingAsHeldEvenBeforeTheReservationExpires',
  'QuotaUsageInspectionNeverCommitsTheCallersInventoryIntentAndHonorsCancellation',
  'QuotaUsageInspectionRefreshesLegacyDisplayMetadataWithoutChangingAllowanceVersion',
  'QuotaUsageInspectionPreservesZeroUnlimitedDisabledAndUnconfiguredModes',
  'QuotaUsageInspectionRetainsAbsentResourceHistoryWithoutRunningItsInventory',
  'QuotaUsageInspectionRetainsPublishedDisplayNameAfterItsOwnerIsRemoved',
  'QuotaUsageInspectionDoesNotUseAnOldInventorySourceVersionAsComplete',
  'QuotaUsageInspectionDoesNotInventMissingProjectionOnAnOlderPolicyNode',
  'QuotaUsageInspectionReportsOverflowAsUnavailableWithoutTruncatingOrMutatingUsage',
  'QuotaUsageInspectionSanitizesPublicationOutageAndRequiresCallerTransaction',
  'QuotaUsageInspectionRejectsTechnicalMaintenanceAuthorityWithoutInventingMembership',
  'QuotaUsageInspectionPausesIncompleteInventoryWithoutItsSourceAndResumesWhenRestored',
  'QuotaUsageHttpAllowsEachOrganizationPermissionAndSerializesInt64AsDecimalString',
  'QuotaUsageHttpDeniesOrganizationMemberWithoutReadOrManagePermission',
  'QuotaUsageHttpPermissionRejectionPrecedesOrganizationRequestCounting',
  'QuotaUsageHttpPublicationOutageReturnsSanitizedUnavailableWithoutInventingCapacity',
  'QuotaUsageHttpIgnoresSuppliedOrganizationQueryAndHeaderAsAuthority',
  'FiniteUploadCapacityCountsDurableHoldsAndOnlyAConfirmedFenceRestoresItsLastSlot',
  'QuotaEngineCannotAdmitFiniteUsageWithIncompleteInventoryAndNoSource',
  'UploadReservationCannotAdmitFiniteUsageWithIncompleteInventoryAndNoSource',
  'UploadRetryReturnsCurrentLifecycleDtoAndRecordsEffectsOnce',
  'SameOperationWithDifferentBytesOrMetadataConflictsAndKeepsOriginalDownload',
  'AuditFailureRollsBackDocumentLedgerAndOutboxAndReleasesUploadAdmission',
  'ExpiredUploadBeforePutIsFencedAndItsHoldReleased',
  'ExpiredUploadAfterPutIsFencedAndLateWriterCannotRestoreItsBytes',
  'StorageFailureRetainsUploadFencingAndHoldForRetryWithoutAttemptCutoff',
  'IneligibleOrganizationPausesMaintenanceAndResumesWhenEligible',
  'AbsentDocumentsModuleStillFencesPreviouslyAdmittedUploadWithoutOriginalMembership',
  'KilledUploadProcessRetainsOnlyItsCommittedEffectsAndFreshWorkerRecovers',
  'ChangedInventorySourceRestartsAndDeduplicatesExistingEffects',
  'OrganizationApiAllowanceIsSharedAcrossProvidersAndIgnoresUntrustedOrganizationHeader',
  'RedisOutageCannotResetOrReplaceThePostgresOrganizationAllowance',
  'ConcurrentCreateIfAbsentAllowsOneWriterAndKeepsItsBytesAndMetadata',
  'HeadMetadataAndLengthMatchConditionallyCreatedObjectAndIndependentReadHash',
  'CancellationSentinelRejectsDelayedAndDuplicateInitialWritesAfterRestart',
  'CurrentEtagCanReplaceProvisionalObjectButStaleIdentityCannotReplaceSentinel',
  'LostSuccessfulCreateResponseIsRecoveredByReadAndConditionalRetry',
  'EnabledBucketVersioningRetainsProvisionalBytesAfterSentinelReplacement',
]
export const requiredTestCaseCount = 47

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const templateRoot = path.join(repositoryRoot, 'templates/trykatch')
const readJson = async file => JSON.parse(await readFile(file, 'utf8'))
const methods = new Set(['get', 'post', 'put', 'patch', 'delete'])

function operations(document) {
  return Object.fromEntries(Object.entries(document.paths ?? {}).flatMap(([route, item]) =>
    Object.entries(item).filter(([method, operation]) => methods.has(method) && operation?.operationId)
      .map(([method, operation]) => [`${method.toUpperCase()} ${route}`, {
        operationId: operation.operationId,
        parameters: operation.parameters,
        requestBody: operation.requestBody,
        responses: operation.responses,
        security: operation.security,
      }])))
}

export async function verifyQuotaPolicyOutput(generatedRoot, namespace, factsFile, reportFile) {
  assert.ok(generatedRoot && namespace && factsFile && reportFile,
    'Expected generated root, namespace, actual module facts file and PostgreSQL TRX report.')
  const canonical = await readJson(path.join(templateRoot, 'web/packages/api-client/openapi/Trykatch.Api.json'))
  const hasWeb = await readFile(path.join(generatedRoot, 'web/package.json')).then(() => true, () => false)
  const generatedOpenApi = path.join(generatedRoot, hasWeb
    ? `web/packages/api-client/openapi/${namespace}.Api.json`
    : `src/API/${namespace}.Api/obj/openapi/${namespace}.Api.json`)
  const actual = await readJson(generatedOpenApi)
  const expectedOperations = operations(canonical)
  const actualOperations = operations(actual)
  assert.deepEqual(Object.keys(actualOperations).sort(), Object.keys(expectedOperations).sort(),
    'Generated public operation set drifted.')
  for (const [operation, expected] of Object.entries(expectedOperations))
    assert.deepEqual(actualOperations[operation], expected, `Generated contract drift: ${operation}`)
  assert.deepEqual(actual.components.schemas, canonical.components.schemas, 'Generated schemas drifted.')

  const usageOperation = actualOperations['GET /api/v1/quotas/usage']
  assert.ok(usageOperation, 'Missing GET /api/v1/quotas/usage.')
  assert.equal(usageOperation.operationId, 'Quotas_CurrentUsage', 'Quota usage operation ID drifted.')
  assert.deepEqual(usageOperation.parameters ?? [], [], 'Quota usage must not accept caller-selected organization parameters.')
  const usageSchemaRef = usageOperation.responses?.['200']?.content?.['application/json']?.schema?.$ref
  assert.equal(usageSchemaRef, '#/components/schemas/OrganizationQuotaUsage', 'Quota usage response schema reference drifted.')
  const usageSchema = actual.components.schemas.OrganizationQuotaUsage
  assert.deepEqual(Object.keys(usageSchema.properties ?? {}), ['resources'], 'Quota usage must expose only aggregate resources.')
  assert.deepEqual(usageSchema.required, ['resources'], 'Quota usage resources must be required.')
  const resourceRef = usageSchema.properties.resources.items.$ref
  assert.equal(resourceRef, '#/components/schemas/OrganizationQuotaUsageResource')
  const resourceSchema = actual.components.schemas.OrganizationQuotaUsageResource
  const safeResourceProperties = [
    'resourceKey', 'name', 'unit', 'mode', 'limit', 'version', 'source', 'committed', 'reserved',
    'reconciliationHeld', 'state', 'installed',
  ]
  assert.deepEqual(Object.keys(resourceSchema.properties ?? {}).sort(), [...safeResourceProperties].sort(),
    'Quota usage resource may include only the approved aggregate metadata and counters.')
  assert.deepEqual([...resourceSchema.required].sort(), [...safeResourceProperties].sort(),
    'Every quota usage resource field must be explicit in the contract.')
  for (const field of ['limit', 'committed', 'reserved', 'reconciliationHeld']) {
    assert.deepEqual(resourceSchema.properties[field].type, ['null', 'string'],
      `Quota usage ${field} must be a nullable decimal string.`)
  }
  assert.deepEqual(resourceSchema.properties.version.type, ['null', 'string'], 'Quota usage version must be nullable.')
  assert.equal(resourceSchema.properties.version.format, 'uuid', 'Quota usage version must remain a GUID string.')
  assert.deepEqual(resourceSchema.properties.source.type, ['null', 'string'], 'Quota usage source must be nullable.')
  for (const field of ['resourceKey', 'name', 'unit', 'mode', 'state'])
    assert.equal(resourceSchema.properties[field].type, 'string', `Quota usage ${field} must be text.`)
  assert.equal(resourceSchema.properties.installed.type, 'boolean')

  const documentsManifest = await readJson(path.join(templateRoot, 'src/Modules/Documents/trykatch.module.json'))
  const facts = await readJson(factsFile)
  const documentsFacts = facts.modules?.find(module => module.id === 'documents')
  assert.ok(documentsFacts?.enabled, 'Documents must be installed and enabled in the generated product.')
  assert.equal(documentsFacts.version, documentsManifest.version, 'Generated Documents module version drifted.')
  const expectedQuotaFacts = (documentsManifest.quotaResources ?? []).map(resource => ({
    key: resource.key,
    name: resource.name,
    description: resource.description,
    unit: resource.unit,
    uploadPolicy: resource.uploadPolicy ?? null,
    inventorySource: resource.inventorySource ?? null,
    removalPolicy: resource.removalPolicy ?? null,
  }))
  assert.deepEqual(documentsFacts.quotaResources, expectedQuotaFacts, 'Generated installed Documents quota declarations drifted.')
  const expectedInstalledModules = (canonical['x-trykatch-modules'] ?? []).map(({ id, version }) => ({ id, version }))
  assert.deepEqual((actual['x-trykatch-modules'] ?? []).map(({ id, version }) => ({ id, version })),
    expectedInstalledModules, 'Generated installed API metadata drifted.')

  const { buildAssistantContract: buildCanonicalAssistantContract } = await import(
    pathToFileURL(path.join(templateRoot, 'tools/generate-assistant-contract.mjs')))
  const { generate: generateActualAssistantContract } = await import(
    pathToFileURL(path.join(generatedRoot, 'tools/generate-assistant-contract.mjs')))
  const expectedAssistant = buildCanonicalAssistantContract(canonical)
  const assistantOutput = path.join(generatedRoot, 'docs/generated/assistant-contract.json')
  const assistantTempRoot = await mkdtemp(path.join(os.tmpdir(), 'trykatch-quota-assistant-'))
  try {
    const regeneratedAssistantPath = path.join(assistantTempRoot, 'assistant-contract.json')
    await generateActualAssistantContract({ openApiPath: generatedOpenApi, outputPath: regeneratedAssistantPath })
    const regeneratedAssistant = await readJson(regeneratedAssistantPath)
    const actualAssistant = await readJson(assistantOutput)
    assert.deepEqual(actualAssistant, regeneratedAssistant,
      'Generated assistant artifact is stale relative to the actual generated OpenAPI.')
    assert.deepEqual(regeneratedAssistant.tools, expectedAssistant.tools, 'Generated assistant tools drifted.')
    assert.deepEqual(regeneratedAssistant.bindings, expectedAssistant.bindings, 'Generated assistant operation bindings drifted.')
    assert.deepEqual(regeneratedAssistant.policy, expectedAssistant.policy, 'Generated assistant policy drifted.')
  } finally {
    await rm(assistantTempRoot, { recursive: true, force: true })
  }

  const report = await readFile(reportFile, 'utf8')
  const countersMatch = report.match(/<Counters\b([^>]*)\/?\s*>/)
  assert.ok(countersMatch, 'Expected real PostgreSQL test result counters in the TRX report.')
  const counters = Object.fromEntries([...countersMatch[1].matchAll(/([\w]+)="(\d+)"/g)]
  .map(([, key, value]) => [key, Number(value)]))
  const requiredCaseCount = requiredTestCaseCount
  assert.equal(counters.total, requiredCaseCount, 'Quota qualification selection is incomplete or includes unexpected cases.')
  assert.equal(counters.executed, requiredCaseCount, 'Quota qualification must execute every selected case.')
  assert.equal(counters.passed, requiredCaseCount, 'Every quota qualification case must pass.')
  assert.equal(counters.failed ?? 0, 0, 'Quota qualification contains failed cases.')
  assert.equal(counters.notExecuted ?? 0, 0, 'Quota qualification contains skipped or unexecuted cases.')
  assert.doesNotMatch(report, /outcome="(?:NotExecuted|Skipped|Inconclusive|Timeout)"/i,
    'Quota qualification may not contain skipped or inconclusive test results.')

  const testCaseCounts = new Map(requiredTestCases.map(name => [name, 1]))
  testCaseCounts.set('QuotaUsageHttpAllowsEachOrganizationPermissionAndSerializesInt64AsDecimalString', 2)
  testCaseCounts.set('IneligibleOrganizationPausesMaintenanceAndResumesWhenEligible', 2)
  testCaseCounts.set('KilledUploadProcessRetainsOnlyItsCommittedEffectsAndFreshWorkerRecovers', 3)
  assert.equal(requiredTestCaseCount, [...testCaseCounts.values()].reduce((sum, count) => sum + count, 0),
    'Internal quota acceptance case count must match its required method list.')
  for (const [name, expectedCount] of testCaseCounts) {
    const actualCount = [...report.matchAll(new RegExp(`testName="[^"]*${name}[^"]*"`, 'g'))].length
    assert.equal(actualCount, expectedCount, `Missing or duplicated required quota acceptance case ${name}.`)
  }

  return `${namespace}: actual generated OpenAPI, quota declaration, assistant contract and ${requiredCaseCount} PostgreSQL cases verified (${hasWeb ? 'React' : 'backend-only'}).`
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  verifyQuotaPolicyOutput(...process.argv.slice(2)).then(message => console.log(message)).catch(error => {
    console.error(error.message)
    process.exitCode = 1
  })
}
