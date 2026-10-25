import { Buffer } from 'node:buffer'
import { createHash, randomUUID } from 'node:crypto'
import { access, mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, isAbsolute, join } from 'node:path'
import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test'
import type { OrganizationQuotaUsage, OrganizationQuotaUsageResource } from '@trykatch/api-client'

declare const process: { env: Record<string, string | undefined> }

const enabled = process.env.TRYKATCH_ACCEPTANCE_QUOTAS === 'true'
const baseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const adminEmail = process.env.TRYKATCH_ACCEPTANCE_ADMIN_EMAIL
const adminPassword = process.env.TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD
const ownerPassword = process.env.TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD
const stage = process.env.TRYKATCH_ACCEPTANCE_QUOTA_STAGE
const statePath = process.env.TRYKATCH_ACCEPTANCE_QUOTA_STATE
const controlDirectory = process.env.TRYKATCH_ACCEPTANCE_QUOTA_CONTROL_DIR
const replicaA = process.env.TRYKATCH_ACCEPTANCE_QUOTA_REPLICA_A_URL ?? 'https://127.0.0.1:8444'
const replicaB = process.env.TRYKATCH_ACCEPTANCE_QUOTA_REPLICA_B_URL ?? 'https://127.0.0.1:8445'

const quotaResourceKey = 'documents.storage-bytes'
const quotaLimit = '64'

type OrganizationName = 'race' | 'failure' | 'expired' | 'throttle'
type QuotaUsage = OrganizationQuotaUsage
type QuotaResource = OrganizationQuotaUsageResource
interface QuotaState {
  ownerEmail: string
  organizations: Record<OrganizationName, string>
  race?: { documentId: string; operationId: string; title: string; payload: string; sha256: string }
  failed?: { title: string; operationId: string }
  expired?: { operationId: string; title: string; payload: string; sha256: string }
  recovered?: { documentId: string; operationId: string; payload: string; sha256: string }
}
interface CreatedOrganization { organization: { id: string }; invitationToken: string }
interface DocumentRecord {
  id: string
  title: string
  fileName: string
  documentType: string
  metadata: { sizeBytes: number; sha256: string }
  lifecycle: { status: string }
}

test.describe('generated production organization quota acceptance', () => {
  // Authentication is real; retain explicit Settings screenshots without recording cookie/token traffic.
  test.use({ trace: 'off' })
  test('quota setup: create four organizations with one shared owner', async ({ browser }) => {
    gate('setup')
    test.setTimeout(900_000)
    const outputPath = absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE')
    const platform = await newContext(browser)
    const owner = await newContext(browser)
    let platformPage: Page | undefined
    try {
      platformPage = await platform.newPage()
      await signIn(platformPage, required(adminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL'), required(adminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD'))
      const runId = `${Date.now()}-${test.info().workerIndex}`
      const ownerEmail = `quota-owner-${runId}@example.test`
      const created = {} as Record<OrganizationName, CreatedOrganization>
      for (const name of ['race', 'failure', 'expired', 'throttle'] as const) {
        created[name] = await createOrganization(platform.request, `Quota ${name} ${runId}`, `quota-${name}-${runId}`, ownerEmail)
      }

      await activateInvitation(owner.request, created.race.invitationToken, required(ownerPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD'))
      await signInApi(owner.request, ownerEmail, ownerPassword!)
      for (const name of ['failure', 'expired', 'throttle'] as const) {
        await acceptInvitation(owner.request, created[name].invitationToken)
      }

      await writeState(outputPath, {
        ownerEmail,
        organizations: {
          race: created.race.organization.id,
          failure: created.failure.organization.id,
          expired: created.expired.organization.id,
          throttle: created.throttle.organization.id,
        },
      })
    } finally {
      await Promise.all([platform.close(), owner.close()])
    }
  })

  test('quota race: two exact-limit uploads through separate replicas admit one winner', async ({ browser }) => {
    gate('race')
    test.setTimeout(600_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.race)
      await expectQuotaReady(context.request)
      const antiforgery = await antiforgeryToken(context.request)
      const payloadA = exactPayload('quota-race-a')
      const payloadB = exactPayload('quota-race-b')
      const operationA = randomUUID()
      const operationB = randomUUID()
      const [responseA, responseB] = await Promise.all([
        upload(context.request, replica('a'), antiforgery, `Quota race A ${randomUUID()}`, payloadA, operationA),
        upload(context.request, replica('b'), antiforgery, `Quota race B ${randomUUID()}`, payloadB, operationB),
      ])
      expect([responseA.status(), responseB.status()].sort()).toEqual([201, 409])
      const winnerResponse = responseA.status() === 201 ? responseA : responseB
      const loserResponse = responseA.status() === 409 ? responseA : responseB
      const winnerPayload = responseA.status() === 201 ? payloadA : payloadB
      const winnerOperationId = responseA.status() === 201 ? operationA : operationB
      const winner = await winnerResponse.json() as DocumentRecord
      await expectProblem(loserResponse, 'quota_exhausted')

      const downloaded = await download(context.request, baseUrl!, winner.id)
      expect(downloaded).toEqual(Buffer.from(winnerPayload))
      const archive = await mutate(context.request, 'POST', `/api/v1/documents/${encodeURIComponent(winner.id)}/archive`)
      expect(archive.status()).toBe(204)
      await expectUsage(context.request, { committed: '64', reserved: '0', reconciliationHeld: '0' })

      const page = await context.newPage()
      try {
        await openUsage(page)
        await expect(page.getByRole('tabpanel', { name: 'Usage' }).getByText('64 bytes').first()).toBeVisible()
      } finally { await page.close() }

      state.race = {
        documentId: winner.id,
        operationId: winnerOperationId,
        title: winner.title,
        payload: winnerPayload,
        sha256: createHash('sha256').update(Buffer.from(winnerPayload)).digest('hex'),
      }
      await writeState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'), state)
    } finally { await context.close() }
  })

  test('quota failed: a real upload audit failure releases its reservation', async ({ browser }) => {
    gate('failed')
    test.setTimeout(300_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.failure)
      await expectQuotaReady(context.request)
      const title = `Quota failed upload ${randomUUID()}`
      const operationId = randomUUID()
      const payload = exactPayload('quota-failed')
      const response = await upload(context.request, baseUrl!, await antiforgeryToken(context.request), title, payload, operationId)
      expect(response.status()).toBeGreaterThanOrEqual(400)
      expect(response.status()).not.toBe(201)
      await expectUsage(context.request, { committed: '0', reserved: '0', reconciliationHeld: '0' })
      const activeDocuments = await json<DocumentRecord[]>(context.request, '/api/v1/documents/?lifecycle=active')
      expect(activeDocuments.some((document) => document.title === title)).toBe(false)
      state.failed = { title, operationId }
      await writeState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'), state)
    } finally { await context.close() }
  })

  test('quota expired: a killed replica leaves a durable hold without a committed document', async ({ browser }) => {
    gate('expired')
    test.setTimeout(900_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    const markers = absolutePath(controlDirectory, 'TRYKATCH_ACCEPTANCE_QUOTA_CONTROL_DIR')
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.expired)
      await expectQuotaReady(context.request)
      const operationId = randomUUID()
      const title = `Quota expired upload ${randomUUID()}`
      const payload = exactPayload('quota-expired')
      const sha256 = createHash('sha256').update(Buffer.from(payload)).digest('hex')
      state.expired = { operationId, title, payload, sha256 }
      await writeState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'), state)
      await writeMarker(markers, 'expired-ready')

      const antiforgery = await antiforgeryToken(context.request)
      let response: APIResponse | undefined
      let failure: unknown
      try {
        response = await upload(context.request, replica('a'), antiforgery, title, payload, operationId, 120_000)
      } catch (error) { failure = error }
      expect(response?.status() === 201, 'the request must fail or receive a non-201 response after the replica is killed').toBe(false)
      if (response) expect(response.status()).toBeGreaterThanOrEqual(400)
      else expect(failure).toBeTruthy()

      await expectUsage(context.request, { committed: '0', reserved: '64', reconciliationHeld: '0' }, replica('b'))
      await writeMarker(markers, 'expired-observed')
    } finally { await context.close() }
  })

  test('quota recovered: expired hold releases and new upload and archived race document remain intact', async ({ browser }) => {
    gate('recovered')
    test.setTimeout(600_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    expect(state.expired, 'the expired stage must persist its operation ID before starting the upload').toBeTruthy()
    expect(state.race, 'the race stage must persist its archived winning document').toBeTruthy()
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.expired)
      await expectUsage(context.request, { committed: '0', reserved: '0', reconciliationHeld: '0' })
      const antiforgery = await antiforgeryToken(context.request)
      const operationId = randomUUID()
      const payload = exactPayload('quota-recovered')
      const recovered = await upload(context.request, baseUrl!, antiforgery, `Quota recovered ${randomUUID()}`, payload, operationId)
      expect(recovered.status()).toBe(201)
      const document = await recovered.json() as DocumentRecord
      expect(await download(context.request, baseUrl!, document.id)).toEqual(Buffer.from(payload))
      await expectUsage(context.request, { committed: '64', reserved: '0', reconciliationHeld: '0' })

      await selectAndVerify(context.request, state.organizations.race)
      const archived = await getDocument(context.request, state.race!.documentId, 'recoverable')
      expect(archived.lifecycle.status).toBe('Archived')
      expect(archived.metadata.sizeBytes).toBe(64)
      expect(archived.metadata.sha256.toLowerCase()).toBe(state.race!.sha256)
      expect(state.race!.payload).toHaveLength(64)
      const raceUsage = await readUsage(context.request)
      expect(quotaResource(raceUsage)).toMatchObject({ committed: '64', reserved: '0', reconciliationHeld: '0' })

      state.recovered = {
        documentId: document.id,
        operationId,
        payload,
        sha256: createHash('sha256').update(Buffer.from(payload)).digest('hex'),
      }
      await writeState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'), state)
    } finally { await context.close() }
  })

  test('quota absent: disabled Documents keeps retained usage visible as paused', async ({ browser }) => {
    gate('absent')
    test.setTimeout(300_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    expect(state.race).toBeTruthy()
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.race)
      const documents = await context.request.get('/api/v1/documents/?lifecycle=active')
      expect(documents.status()).toBe(404)
      const usage = await readUsage(context.request)
      expect(quotaResource(usage)).toMatchObject({ name: 'Document storage', committed: '64', installed: false, state: 'Paused' })
      const page = await context.newPage()
      try {
        await openUsage(page)
        const panel = page.getByRole('tabpanel', { name: 'Usage' })
        await expect(panel.getByText(/^(Paused|En pause)$/)).toBeVisible()
        await expect(panel.getByText('64 bytes').first()).toBeVisible()
      } finally { await page.close() }
    } finally { await context.close() }
  })

  test('quota restored: archived upload survives module restoration and Settings at desktop and mobile sizes', async ({ browser }) => {
    gate('restored')
    test.setTimeout(300_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    expect(state.race).toBeTruthy()
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.race)
      const archived = await getDocument(context.request, state.race!.documentId, 'recoverable')
      expect(archived.lifecycle.status).toBe('Archived')
      expect(archived.metadata.sizeBytes).toBe(64)
      expect(archived.metadata.sha256.toLowerCase()).toBe(state.race!.sha256)
      expect(Buffer.byteLength(state.race!.payload, 'utf8')).toBe(64)
      const restore = await mutate(context.request, 'POST', `/api/v1/documents/${encodeURIComponent(state.race!.documentId)}/restore`)
      expect(restore.status()).toBe(204)
      expect(await download(context.request, baseUrl!, state.race!.documentId)).toEqual(Buffer.from(state.race!.payload))
      const rearchive = await mutate(context.request, 'POST', `/api/v1/documents/${encodeURIComponent(state.race!.documentId)}/archive`)
      expect(rearchive.status()).toBe(204)
      const archivedAgain = await getDocument(context.request, state.race!.documentId, 'recoverable')
      expect(archivedAgain.lifecycle.status).toBe('Archived')
      await expectUsage(context.request, { committed: '64', reserved: '0', reconciliationHeld: '0' })

      const page = await context.newPage()
      try {
        await page.setViewportSize({ width: 1440, height: 900 })
        await openUsage(page)
        const desktop = await page.screenshot()
        await test.info().attach('quota-settings-desktop.png', { body: desktop, contentType: 'image/png' })
        await page.setViewportSize({ width: 320, height: 800 })
        await page.reload()
        await page.getByRole('tab', { name: 'Usage' }).click()
        const mobile = await page.screenshot()
        await test.info().attach('quota-settings-mobile.png', { body: mobile, contentType: 'image/png' })
        await expect(page.getByRole('tabpanel', { name: 'Usage' }).getByText('64 bytes').first()).toBeVisible()
      } finally { await page.close() }
    } finally { await context.close() }
  })

  test('quota throttle: retained organization request limits survive both replica restarts', async ({ browser }) => {
    gate('throttle')
    test.setTimeout(900_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_QUOTA_STATE'))
    const markers = absolutePath(controlDirectory, 'TRYKATCH_ACCEPTANCE_QUOTA_CONTROL_DIR')
    const context = await ownerContext(browser, state.ownerEmail)
    try {
      await selectAndVerify(context.request, state.organizations.throttle)
      await writeMarker(markers, 'throttle-ready')
      await waitForMarker(markers, 'throttle-enabled', 300_000)

      const first = await context.request.get(`${replica('a')}/api/v1/quotas/usage`)
      expect(first.status()).toBe(200)
      const limited = await context.request.get(`${replica('b')}/api/v1/quotas/usage`)
      expect(limited.status()).toBe(429)
      expectPositiveRetryAfter(limited)

      await writeMarker(markers, 'throttle-restart-ready')
      await waitForMarker(markers, 'throttle-restarted', 300_000)
      for (const instance of ['a', 'b'] as const) {
        const retained = await context.request.get(`${replica(instance)}/api/v1/quotas/usage`)
        expect(retained.status()).toBe(429)
        expectPositiveRetryAfter(retained)
      }
    } finally { await context.close() }
  })
})

function gate(expectedStage: string) {
  test.skip(!enabled || stage !== expectedStage || !baseUrl, `Runs only for the ${expectedStage} quota acceptance stage.`)
}

async function newContext(browser: Browser): Promise<BrowserContext> {
  const origin = required(baseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL')
  return browser.newContext({ baseURL: origin, ignoreHTTPSErrors: origin.startsWith('https://') })
}

async function ownerContext(browser: Browser, email: string) {
  const context = await newContext(browser)
  try { await signInApi(context.request, email, required(ownerPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD')); return context }
  catch (error) { await context.close(); throw error }
}

async function signIn(page: Page, email: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('Email address').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/(overview|dashboard)$/)
}

async function signInApi(request: APIRequestContext, email: string, password: string) {
  const response = await mutate(request, 'POST', '/api/v1/auth/login', { email, password, rememberMe: false })
  expect(response.status(), 'the real login endpoint should accept the account').toBe(200)
}

async function createOrganization(request: APIRequestContext, name: string, slug: string, administratorEmail: string) {
  const response = await mutate(request, 'POST', '/api/v1/tenants', { name, slug, administratorEmail })
  expect(response.status(), 'the real platform endpoint should create the organization').toBe(201)
  return await response.json() as CreatedOrganization
}

async function activateInvitation(request: APIRequestContext, token: string, password: string) {
  const response = await mutate(request, 'POST', '/api/v1/invitations/activate', { token, firstName: 'Quota', lastName: 'Owner', password })
  expect(response.status(), 'the real invitation activation endpoint should activate the owner').toBe(200)
}

async function acceptInvitation(request: APIRequestContext, token: string) {
  const response = await mutate(request, 'POST', '/api/v1/invitations/accept', { token })
  expect(response.status(), 'the authenticated owner should accept the organization invitation').toBe(200)
}

async function selectAndVerify(request: APIRequestContext, organizationId: string) {
  const selected = await mutate(request, 'POST', '/api/v1/workspace/select', { organizationId, remember: true })
  expect(selected.status()).toBe(204)
  const current = await json<{ organizationId: string }>(request, '/api/v1/workspace/current')
  expect(current.organizationId).toBe(organizationId)
}

async function mutate(request: APIRequestContext, method: 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown) {
  const token = await antiforgeryToken(request)
  return request.fetch(path, { method, data, headers: { 'X-CSRF-TOKEN': token } })
}

async function antiforgeryToken(request: APIRequestContext) {
  const response = await request.get('/api/v1/auth/antiforgery')
  expect(response.status(), 'the real antiforgery endpoint should return a token').toBe(200)
  const body = await response.json() as { token: string }
  expect(body.token).toBeTruthy()
  return body.token
}

async function json<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(path)
  expect(response.status(), `${path} returned ${response.status()}`).toBe(200)
  return await response.json() as T
}

async function readUsage(request: APIRequestContext, origin = required(baseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL')): Promise<QuotaUsage> {
  const response = await request.get(`${origin}/api/v1/quotas/usage`)
  expect(response.status(), `quota usage returned ${response.status()}`).toBe(200)
  return await response.json() as QuotaUsage
}

async function expectQuotaReady(request: APIRequestContext, origin = required(baseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL')) {
  await expect.poll(async () => {
    const response = await request.get(`${origin}/api/v1/quotas/usage`)
    if (response.status() !== 200) return false
    const resource = quotaResource(await response.json() as QuotaUsage)
    return resource.state === 'Ready' && resource.mode === 'Finite' && resource.limit === quotaLimit && resource.installed
  }, { timeout: 120_000, intervals: [500, 1_000, 2_000], message: 'the operator should have installed a ready 64-byte Documents quota' }).toBe(true)
}

async function expectUsage(request: APIRequestContext, expected: Partial<Pick<QuotaResource, 'committed' | 'reserved' | 'reconciliationHeld'>>, origin = required(baseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL')) {
  let last: QuotaResource | undefined
  await expect.poll(async () => {
    const response = await request.get(`${origin}/api/v1/quotas/usage`)
    if (response.status() !== 200) return false
    last = quotaResource(await response.json() as QuotaUsage)
    return Object.entries(expected).every(([key, value]) => last?.[key as keyof QuotaResource] === value)
  }, { timeout: 120_000, intervals: [500, 1_000, 2_000], message: `quota usage should match ${JSON.stringify(expected)}` }).toBe(true)
  expect(last).toMatchObject(expected)
}

async function upload(request: APIRequestContext, origin: string, token: string, title: string, payload: string, operationId: string, timeout = 60_000) {
  const bytes = Buffer.from(payload, 'utf8')
  expect(bytes.byteLength).toBe(64)
  return request.post(`${origin}/api/v1/documents/`, {
    timeout,
    headers: { 'X-CSRF-TOKEN': token },
    multipart: {
      title,
      description: 'Production quota acceptance upload.',
      documentType: 'other',
      operationId,
      file: { name: 'quota-proof.txt', mimeType: 'text/plain', buffer: bytes },
    },
  })
}

async function download(request: APIRequestContext, origin: string, id: string) {
  const response = await request.get(`${origin}/api/v1/documents/${encodeURIComponent(id)}/content`)
  expect(response.status(), 'the actual Documents endpoint should return the uploaded bytes').toBe(200)
  return response.body()
}

async function getDocument(request: APIRequestContext, id: string, lifecycle: 'active' | 'recoverable') {
  const documents = await json<DocumentRecord[]>(request, `/api/v1/documents/?lifecycle=${lifecycle}`)
  const document = documents.find((candidate) => candidate.id === id)
  expect(document, `document ${id} should remain in the ${lifecycle} collection`).toBeTruthy()
  return document!
}

async function expectProblem(response: APIResponse, title: string) {
  const problem = await response.json() as { title?: string }
  expect(problem.title).toBe(title)
  expect(JSON.stringify(problem)).not.toMatch(/select|exception|stack|database|connection string/i)
}

function quotaResource(usage: QuotaUsage): QuotaResource {
  const resource = usage.resources.find((candidate) => candidate.resourceKey === quotaResourceKey)
  expect(resource, `the real quota catalog should include ${quotaResourceKey}`).toBeTruthy()
  return resource!
}

function exactPayload(prefix: string) {
  const value = `${prefix}-${randomUUID()}`
  return value.padEnd(64, '!')
}

function replica(instance: 'a' | 'b') {
  return instance === 'a' ? required(replicaA, 'TRYKATCH_ACCEPTANCE_QUOTA_REPLICA_A_URL') : required(replicaB, 'TRYKATCH_ACCEPTANCE_QUOTA_REPLICA_B_URL')
}

async function openUsage(page: Page) {
  await page.goto('/settings')
  await expect(page.getByRole('heading', { name: 'Settings', exact: true })).toBeVisible()
  await page.getByRole('tab', { name: /^(Usage|Utilisation)$/ }).click()
  await expect(page.getByRole('tabpanel', { name: /^(Usage|Utilisation)$/ })).toBeVisible()
  await expect(page.getByRole('tabpanel', { name: /^(Usage|Utilisation)$/ }).getByRole('heading', { name: 'Document storage' })).toBeVisible()
}

async function writeState(path: string, state: QuotaState) {
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, `${JSON.stringify(state, null, 2)}\n`, { mode: 0o600 })
}

async function readState(path: string): Promise<QuotaState> {
  const state = JSON.parse(await readFile(path, 'utf8')) as QuotaState
  expect(state.ownerEmail).toMatch(/^[^@\s]+@example\.test$/)
  for (const organizationId of Object.values(state.organizations)) expect(organizationId).toMatch(/^[0-9a-f-]{36}$/i)
  if (state.race) {
    expect(state.race.documentId).toMatch(/^[0-9a-f-]{36}$/i)
    expect(state.race.operationId).toMatch(/^[0-9a-f-]{36}$/i)
    expect(state.race.payload).toHaveLength(64)
    expect(state.race.sha256).toMatch(/^[0-9a-f]{64}$/)
  }
  return state
}

async function writeMarker(directory: string, marker: string) {
  await mkdir(directory, { recursive: true })
  await writeFile(join(directory, marker), '', { mode: 0o600 })
}

async function waitForMarker(directory: string, marker: string, timeout: number) {
  const path = join(directory, marker)
  await expect.poll(async () => {
    try { await access(path); return true }
    catch { return false }
  }, { timeout, intervals: [250, 500, 1_000, 2_000], message: `the acceptance harness should create ${marker}` }).toBe(true)
}

function expectPositiveRetryAfter(response: APIResponse) {
  const value = response.headers()['retry-after']
  expect(value, 'the real limiter should return Retry-After').toMatch(/^[1-9]\d*$/)
}

function required(value: string | undefined, name: string) {
  expect(value, name).toBeTruthy()
  return value!
}

function absolutePath(value: string | undefined, name: string) {
  const path = required(value, name)
  expect(isAbsolute(path), `${name} must be an absolute path`).toBe(true)
  return path
}
