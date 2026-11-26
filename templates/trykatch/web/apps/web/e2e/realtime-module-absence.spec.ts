import { readFile } from 'node:fs/promises'
import { expect, test, type APIRequestContext } from '@playwright/test'

declare const process: { env: Record<string, string | undefined> }

const realtimeAcceptanceEnabled = process.env.TRYKATCH_ACCEPTANCE_REALTIME === 'true'
const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const organizationAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD
const realtimeStatePath = process.env.TRYKATCH_ACCEPTANCE_REALTIME_STATE

test.describe('packaged application with Projects removed', () => {
  test.skip(!realtimeAcceptanceEnabled, 'Set TRYKATCH_ACCEPTANCE_REALTIME=true to run the post-removal acceptance stage.')

  test('keeps retained realtime history inaccessible through an absent module', async ({ browser }) => {
    test.setTimeout(60_000)
    expect(acceptanceBaseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL').toBeTruthy()
    expect(organizationAdminPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD').toBeTruthy()
    expect(realtimeStatePath, 'TRYKATCH_ACCEPTANCE_REALTIME_STATE').toBeTruthy()

    const fixture = JSON.parse(await readFile(realtimeStatePath!, 'utf8')) as RealtimeAcceptanceFixture
    expect(Object.keys(fixture).sort()).toEqual(['organizationId', 'ownerEmail'])
    expect(fixture.ownerEmail).toMatch(/^[^\s@]+@[^\s@]+$/)
    expect(fixture.organizationId).toMatch(/^[0-9a-f-]{36}$/i)

    const context = await browser.newContext({ baseURL: acceptanceBaseUrl })
    try {
      await signInAndSelectOrganization(context.request, fixture)

      const projectList = await context.request.get('/api/v1/projects?page=1&pageSize=100&lifecycle=active')
      expect(projectList.status(), 'the uninstalled Projects endpoint should be absent').toBe(404)

      const state = await getJson<RealtimeStateResponse>(context.request, '/api/v1/realtime/state')
      expect(state.enabled).toBe(true)
      expect(state.generation).toMatch(/^[0-9a-f-]{36}$/i)

      const history = await getJson<RealtimeHistoryResponse>(context.request, '/api/v1/realtime/history')
      expect(history.generation).toBe(state.generation)
      expect(history.hasMore).toBe(false)
      expect(history.items.filter((item) => item.moduleId === 'projects')).toHaveLength(0)

      const page = await context.newPage()
      await page.goto('/overview')
      await expect(page.getByRole('heading', { name: /overview/i })).toBeVisible()
      await expect(page.getByRole('link', { name: 'Projects', exact: true })).toHaveCount(0)
    } finally {
      await context.close()
    }
  })
})

async function signInAndSelectOrganization(request: APIRequestContext, fixture: RealtimeAcceptanceFixture) {
  let csrf = await getJson<{ token: string }>(request, '/api/v1/auth/antiforgery')
  const login = await request.post('/api/v1/auth/login', {
    data: { email: fixture.ownerEmail, password: organizationAdminPassword, rememberMe: false },
    headers: { 'X-CSRF-TOKEN': csrf.token },
  })
  expect(login.status(), 'the retained organization owner should sign in through the public authentication endpoint').toBe(200)
  const session = await getJson<SessionResponse>(request, '/api/v1/auth/session')
  expect(session.email).toBe(fixture.ownerEmail)

  csrf = await getJson<{ token: string }>(request, '/api/v1/auth/antiforgery')
  const selection = await request.post('/api/v1/workspace/select', {
    data: { organizationId: fixture.organizationId, remember: true },
    headers: { 'X-CSRF-TOKEN': csrf.token },
  })
  expect(selection.status(), 'the retained organization owner should select the recorded workspace').toBe(204)

  const current = await getJson<CurrentWorkspaceResponse>(request, '/api/v1/workspace/current')
  expect(current.organizationId).toBe(fixture.organizationId)
}

async function getJson<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(path)
  expect(response.ok(), `${path} returned ${response.status()}`).toBe(true)
  return await response.json() as T
}

interface RealtimeAcceptanceFixture {
  ownerEmail: string
  organizationId: string
}

interface SessionResponse {
  email: string
}

interface CurrentWorkspaceResponse {
  organizationId: string
}

interface RealtimeStateResponse {
  enabled: boolean
  generation: string
  cursor: string
}

interface RealtimeHistoryResponse {
  items: Array<{ moduleId: string; eventKey: string; schemaVersion: number }>
  generation: string
  cursor: string
  hasMore: boolean
}
