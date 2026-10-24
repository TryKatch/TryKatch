import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test'
import { mkdir, writeFile } from 'node:fs/promises'
import { dirname } from 'node:path'

declare const process: { env: Record<string, string | undefined> }

const realtimeAcceptanceEnabled = process.env.TRYKATCH_ACCEPTANCE_REALTIME === 'true'
const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const platformAdminEmail = process.env.TRYKATCH_ACCEPTANCE_ADMIN_EMAIL
const platformAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD
const organizationAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD
const realtimeStatePath = process.env.TRYKATCH_ACCEPTANCE_REALTIME_STATE

let moduleAbsenceFixture: { ownerEmail: string; organizationId: string } | undefined

test.describe('packaged application realtime acceptance', () => {
  test.skip(!realtimeAcceptanceEnabled, 'Set TRYKATCH_ACCEPTANCE_REALTIME=true to run against the packaged application.')

  test('reconciles project changes, revocation, and workspace switches across real sessions', async ({ browser }) => {
    test.setTimeout(180_000)
    expect(acceptanceBaseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL').toBeTruthy()
    expect(platformAdminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL').toBeTruthy()
    expect(platformAdminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD').toBeTruthy()
    expect(organizationAdminPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD').toBeTruthy()

    const runId = `${Date.now()}-${test.info().workerIndex}`
    const slug = `realtime-${runId}`
    const ownerEmail = `realtime-owner-${runId}@example.test`
    const memberEmail = `realtime-member-${runId}@example.test`
    const projectNames = {
      live: `Live update ${runId}`,
      recovered: `Recovered update ${runId}`,
      revoked: `Revoked delivery ${runId}`,
      alpha: `Alpha only ${runId}`,
      beta: `Beta only ${runId}`,
    }

    const platformContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const ownerContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const memberContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    let ownerActivationContext: Awaited<ReturnType<typeof browser.newContext>> | undefined
    let memberActivationContext: Awaited<ReturnType<typeof browser.newContext>> | undefined

    try {
      const platformPage = await platformContext.newPage()
      await signIn(platformPage, platformAdminEmail!, platformAdminPassword!)
      const platformRequest = platformContext.request

      const alpha = await createOrganization(platformRequest, `Realtime Alpha ${runId}`, `${slug}-alpha`, ownerEmail)
      const beta = await createOrganization(platformRequest, `Realtime Beta ${runId}`, `${slug}-beta`, ownerEmail)

      ownerActivationContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
      await activateInvitation(ownerActivationContext.request, alpha.invitationToken, 'Realtime', 'Owner')
      await ownerActivationContext.close()
      ownerActivationContext = undefined

      const ownerPage = await ownerContext.newPage()
      const ownerTransport = observeWebSockets(ownerPage)
      await signIn(ownerPage, ownerEmail, organizationAdminPassword!)
      expect((await mutate(ownerContext.request, 'POST', '/api/v1/invitations/accept', { token: beta.invitationToken })).status()).toBe(200)
      await selectWorkspace(ownerContext.request, alpha.organization.id)
      // Establish the initial fixture through navigation even when Alpha is already cached.
      // The later Alpha/Beta transitions exercise the mounted lifecycle without reloading.
      await refreshCurrentWorkspace(ownerPage, alpha.organization.id, true)

      const invitationResponse = await mutate(ownerContext.request, 'POST', '/api/v1/invitations', { email: memberEmail })
      expect(invitationResponse.status()).toBe(200)
      const invitation = await invitationResponse.json() as InvitationResponse
      const invitationToken = decodeURIComponent(new URL(invitation.invitationUrl).pathname.split('/').at(-1) ?? '')
      expect(invitationToken).toBeTruthy()

      memberActivationContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
      await activateInvitation(memberActivationContext.request, invitationToken, 'Realtime', 'Member')
      await memberActivationContext.close()
      memberActivationContext = undefined

      const memberPage = await memberContext.newPage()
      await installRealtimeDisconnectControl(memberPage)
      const memberTransport = observeWebSockets(memberPage)
      const historyReads = observeHistoryReads(memberPage)
      await signIn(memberPage, memberEmail, organizationAdminPassword!)
      await expect(memberPage).toHaveURL(/\/overview$/)
      await expect(memberPage.getByRole('link', { name: 'Projects', exact: true })).toBeVisible()
      await memberPage.goto('/projects')
      await expect(memberPage.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()
      await expect(memberPage.getByText('No projects')).toBeVisible()

      await ownerPage.goto('/projects')
      await expect(ownerPage.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()

      await expect.poll(
        () => memberTransport.lastSubscribedConnection,
        { timeout: 30_000, message: 'the packaged application should establish a real SignalR WebSocket' },
      ).toBeGreaterThan(0)

      const beforeLiveEvent = await getJson<RealtimeStateResponse>(ownerContext.request, '/api/v1/realtime/state')
      expect(beforeLiveEvent.enabled).toBe(true)
      await createProject(ownerContext.request, projectNames.live)
      await expect.poll(
        () => hasRealtimeEvent(ownerContext.request, beforeLiveEvent.cursor),
        { timeout: 30_000, message: 'the committed project.created event should become available through HTTP history' },
      ).toBe(true)
      await expect(memberPage.getByRole('row', { name: new RegExp(projectNames.live) })).toBeVisible({ timeout: 30_000 })
      await expect.poll(
        () => memberTransport.projectNotifications,
        { timeout: 30_000, message: 'SignalR should deliver the projects.created invalidation frame to the second browser' },
      ).toBeGreaterThan(0)
      await expect.poll(() => historyReads.projectEvents, { timeout: 30_000 }).toBeGreaterThan(0)

      const historyEventsBeforeGap = historyReads.projectEvents
      const openedBeforeGap = memberTransport.opened
      const closedBeforeGap = memberTransport.closed
      await memberContext.setOffline(true)
      // Chromium's HTTP offline emulation does not reliably close existing WebSockets.
      // Close the actual browser transport while new HTTP negotiation is unavailable.
      await memberPage.evaluate(() => {
        (window as unknown as { __trykatchAcceptanceCloseRealtime: () => void }).__trykatchAcceptanceCloseRealtime()
      })
      await expect.poll(() => memberTransport.closed, { timeout: 30_000 }).toBeGreaterThan(closedBeforeGap)

      const beforeGapEvent = await getJson<RealtimeStateResponse>(ownerContext.request, '/api/v1/realtime/state')
      await createProject(ownerContext.request, projectNames.recovered)
      await expect.poll(
        () => hasRealtimeEvent(ownerContext.request, beforeGapEvent.cursor),
        { timeout: 30_000, message: 'the disconnected browser should still be able to recover the committed HTTP history later' },
      ).toBe(true)
      await expect(memberPage.getByRole('row', { name: new RegExp(projectNames.recovered) })).toHaveCount(0)

      await memberContext.setOffline(false)
      await expect(memberPage.getByRole('row', { name: new RegExp(projectNames.recovered) })).toBeVisible({ timeout: 30_000 })
      await expect.poll(() => historyReads.projectEvents, { timeout: 30_000 }).toBeGreaterThan(historyEventsBeforeGap)
      await expect.poll(() => memberTransport.opened, { timeout: 30_000 }).toBeGreaterThan(openedBeforeGap)

      const members = await getJson<MemberResponse[]>(ownerContext.request, '/api/v1/members')
      const member = members.find((item) => item.email === memberEmail)
      expect(member, 'the invited member should have an active organization membership').toBeTruthy()
      const closedBeforeRevocation = memberTransport.closed
      expect((await mutate(ownerContext.request, 'POST', `/api/v1/members/${member!.id}/archive`)).status()).toBe(204)
      await expect.poll(() => memberTransport.closed, { timeout: 30_000 }).toBeGreaterThan(closedBeforeRevocation)
      await expect(memberPage.getByRole('row', { name: new RegExp(projectNames.live) })).toHaveCount(0, { timeout: 30_000 })
      await expect(memberPage.getByRole('row', { name: new RegExp(projectNames.recovered) })).toHaveCount(0)

      const revokedProject = await createProject(ownerContext.request, projectNames.revoked)
      expect(revokedProject.id).toBeTruthy()
      await expect.poll(async () => {
        const response = await afterRateLimit(() => memberContext.request.get('/api/v1/projects?page=1&pageSize=100&lifecycle=active'))
        return [403, 409].includes(response.status())
      }, { timeout: 90_000 }).toBe(true)
      await expect(memberPage.getByRole('row', { name: new RegExp(projectNames.revoked) })).toHaveCount(0)

      await selectWorkspace(ownerContext.request, beta.organization.id)
      await refreshCurrentWorkspace(ownerPage, beta.organization.id)
      await createProject(ownerContext.request, projectNames.beta)
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.beta) })).toBeVisible()
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.live) })).toHaveCount(0)
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.recovered) })).toHaveCount(0)

      const ownerConnectionsBeforeAlpha = ownerTransport.opened
      await selectWorkspace(ownerContext.request, alpha.organization.id)
      await refreshCurrentWorkspace(ownerPage, alpha.organization.id)
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.live) })).toBeVisible()
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.recovered) })).toBeVisible()
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.beta) })).toHaveCount(0)
      moduleAbsenceFixture = { ownerEmail, organizationId: alpha.organization.id }

      await expect.poll(() => ownerTransport.lastSubscribedConnection, {
        timeout: 30_000, message: 'the replacement Alpha socket should finish authorized subscription before publication',
      }).toBeGreaterThan(ownerConnectionsBeforeAlpha)
      const notificationsBeforeAlpha = ownerTransport.projectNotifications
      await createProject(ownerContext.request, projectNames.alpha)
      await expect.poll(() => ownerTransport.projectNotifications, {
        timeout: 30_000, message: 'the current Alpha lifecycle should receive a real notification before workspace clear',
      }).toBeGreaterThan(notificationsBeforeAlpha)
      // A real domain refetch can be throttled after the push; HTTP repair must retry it after the window resets.
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.alpha) })).toBeVisible({ timeout: 90_000 })
      const closedBeforeClear = ownerTransport.closed
      expect((await mutate(ownerContext.request, 'DELETE', '/api/v1/workspace/current')).status()).toBe(204)
      const missingWorkspace = await afterRateLimit(() => ownerContext.request.get('/api/v1/realtime/history'))
      expect(missingWorkspace.status()).toBe(409)
      expect((await missingWorkspace.json() as { title: string }).title).toBe('Workspace context required')
      expect((await afterRateLimit(() => ownerContext.request.get('/api/v1/auth/session'))).status()).toBe(200)
      await expect.poll(() => ownerTransport.closed, { timeout: 30_000 }).toBeGreaterThan(closedBeforeClear)
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.live) })).toHaveCount(0, { timeout: 30_000 })
      await expect(ownerPage.getByRole('row', { name: new RegExp(projectNames.recovered) })).toHaveCount(0)
    } finally {
      await Promise.all([
        ownerActivationContext?.close(),
        memberActivationContext?.close(),
        platformContext.close(),
        ownerContext.close(),
        memberContext.close(),
      ])
    }
  })

  test.afterEach(async ({}, testInfo) => {
    if (testInfo.status !== 'passed' || !moduleAbsenceFixture) return
    expect(realtimeStatePath, 'TRYKATCH_ACCEPTANCE_REALTIME_STATE').toBeTruthy()
    await mkdir(dirname(realtimeStatePath!), { recursive: true })
    await writeFile(realtimeStatePath!, `${JSON.stringify(moduleAbsenceFixture, null, 2)}\n`, { mode: 0o600 })
  })
})

async function signIn(page: Page, email: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('Email address').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/(overview|dashboard)$/)
}

async function createOrganization(request: APIRequestContext, name: string, slug: string, administratorEmail: string) {
  const response = await mutate(request, 'POST', '/api/v1/tenants', { name, slug, administratorEmail })
  expect(response.status()).toBe(201)
  return await response.json() as CreateOrganizationResponse
}

async function activateInvitation(request: APIRequestContext, token: string, firstName: string, lastName: string) {
  const response = await mutate(request, 'POST', '/api/v1/invitations/activate', {
    token,
    firstName,
    lastName,
    password: organizationAdminPassword,
  })
  expect(response.status()).toBe(200)
}

async function selectWorkspace(request: APIRequestContext, organizationId: string) {
  const response = await mutate(request, 'POST', '/api/v1/workspace/select', { organizationId, remember: true })
  expect(response.status()).toBe(204)
}

async function afterRateLimit(operation: () => Promise<APIResponse>) {
  // This multi-session journey can exhaust the existing one-minute request window.
  // Retry only rejected requests; the limiter runs before endpoints. Never replay an admitted mutation.
  const deadline = Date.now() + 65_000
  while (true) {
    const response = await operation()
    if (response.status() !== 429) return response
    if (Date.now() >= deadline) throw new Error('Public request remained rate limited beyond one request window.')
    await new Promise((resolve) => setTimeout(resolve, 2_000))
  }
}

async function refreshCurrentWorkspace(page: Page, organizationId: string, reload = false) {
  const workspaceResponse = page.waitForResponse(async (response) => {
    if (new URL(response.url()).pathname !== '/api/v1/workspace/current' || !response.ok()) return false
    return (await response.json() as CurrentWorkspaceResponse).organizationId === organizationId
  }, { timeout: 30_000 })
  if (reload) await page.reload()
  else await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  await workspaceResponse
}

async function createProject(request: APIRequestContext, name: string) {
  const response = await mutate(request, 'POST', '/api/v1/projects', {
    name,
    description: 'Created by the packaged realtime acceptance journey.',
  })
  expect(response.status()).toBe(201)
  return await response.json() as ProjectResponse
}

async function mutate(request: APIRequestContext, method: 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown) {
  const antiforgery = await getJson<{ token: string }>(request, '/api/v1/auth/antiforgery')
  return afterRateLimit(() => request.fetch(path, {
    method,
    data,
    headers: { 'X-CSRF-TOKEN': antiforgery.token },
  }))
}

async function getJson<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await afterRateLimit(() => request.get(path))
  expect(response.ok(), `${path} returned ${response.status()}`).toBe(true)
  return await response.json() as T
}

async function hasRealtimeEvent(request: APIRequestContext, cursor: string) {
  const response = await afterRateLimit(() => request.get('/api/v1/realtime/history', { params: { cursor } }))
  if (!response.ok()) return false
  const history = await response.json() as RealtimeHistoryResponse
  return history.items.some((item) => item.moduleId === 'projects'
    && item.eventKey === 'projects.created'
    && item.schemaVersion === 1)
}

async function installRealtimeDisconnectControl(page: Page) {
  await page.addInitScript(() => {
    const sockets = new Set<WebSocket>()
    window.WebSocket = new Proxy(window.WebSocket, {
      construct(target, args) {
        const socket = Reflect.construct(target, args) as WebSocket
        if (new URL(String(args[0]), window.location.href).pathname === '/api/v1/realtime/hub') {
          sockets.add(socket)
          socket.addEventListener('close', () => sockets.delete(socket))
        }
        return socket
      },
    })
    Object.assign(window, {
      __trykatchAcceptanceCloseRealtime: () => {
        for (const socket of sockets) socket.close(1000, 'acceptance disconnect')
      },
    })
  })
}

function observeWebSockets(page: Page) {
  const observed = { opened: 0, closed: 0, projectNotifications: 0, lastSubscribedConnection: 0 }
  page.on('websocket', (socket) => {
    if (!socket.url().includes('/api/v1/realtime/hub')) return
    const serial = ++observed.opened
    const subscriptions = new Set<string>()
    socket.on('framesent', ({ payload }) => {
      for (const invocation of hubMessages(payload)) {
        if (invocation.type === 1 && invocation.target === 'ReplaceSubscriptions' && invocation.invocationId)
          subscriptions.add(invocation.invocationId)
      }
    })
    socket.on('framereceived', ({ payload }) => {
      for (const invocation of hubMessages(payload)) {
        if (invocation.type === 3 && invocation.invocationId && subscriptions.delete(invocation.invocationId) && !invocation.error)
          observed.lastSubscribedConnection = serial
        if (invocation.type === 1 && invocation.target === 'invalidate'
          && invocation.arguments?.some((notification) => notification.moduleId === 'projects'
            && notification.eventKey === 'projects.created'
            && notification.schemaVersion === 1)) observed.projectNotifications += 1
      }
    })
    socket.on('close', () => {
      observed.closed += 1
      if (observed.lastSubscribedConnection === serial) observed.lastSubscribedConnection = 0
    })
  })
  return observed
}

function hubMessages(payload: string | { toString(encoding: 'utf8'): string }) {
  const frame = typeof payload === 'string' ? payload : payload.toString('utf8')
  const messages: Array<{
    type?: number; target?: string; invocationId?: string; error?: string
    arguments?: Array<{ moduleId?: string; eventKey?: string; schemaVersion?: number }>
  }> = []
  for (const message of frame.split('\u001e')) {
    if (!message) continue
    try { const value = JSON.parse(message); if (value && typeof value === 'object') messages.push(value) }
    catch { /* Handshake/control frames need not contain an invocation. */ }
  }
  return messages
}

function observeHistoryReads(page: Page) {
  const observed = { projectEvents: 0 }
  page.on('response', async (response) => {
    if (new URL(response.url()).pathname !== '/api/v1/realtime/history' || !response.ok()) return
    try {
      const history = await response.json() as RealtimeHistoryResponse
      if (history.items.some((item) => item.moduleId === 'projects'
        && item.eventKey === 'projects.created' && item.schemaVersion === 1)) observed.projectEvents += 1
    } catch (error) {
      // Chromium can discard a completed response body during the deliberate navigation/cleanup.
      // Such a response cannot satisfy the history assertion; other failures still fail the test.
      const message = String(error)
      if (!page.isClosed() && !message.includes('No resource with given identifier found')
        && !message.includes('Response body is not available')
        && !message.includes('Target page, context or browser has been closed')) throw error
    }
  })
  return observed
}

interface CreateOrganizationResponse {
  organization: { id: string }
  invitationToken: string
}

interface InvitationResponse {
  invitation: { id: string }
  invitationUrl: string
}

interface MemberResponse {
  id: string
  email: string
}

interface ProjectResponse {
  id: string
}

interface CurrentWorkspaceResponse {
  organizationId: string
}

interface RealtimeStateResponse {
  enabled: boolean
  cursor: string
}

interface RealtimeHistoryResponse {
  items: Array<{
    moduleId: string
    eventKey: string
    schemaVersion: number
  }>
}
