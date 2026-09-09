import { expect, test, type APIRequestContext, type APIResponse } from '@playwright/test'

const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const platformAdminEmail = process.env.TRYKATCH_ACCEPTANCE_ADMIN_EMAIL
const platformAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD
const platformManagerPassword = process.env.TRYKATCH_ACCEPTANCE_PLATFORM_MANAGER_PASSWORD
const organizationAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD

test.describe('generated production application', () => {
  test.skip(!acceptanceBaseUrl, 'Runs only against a packaged application started by the acceptance harness.')

  test('enforces the critical platform and organization journeys', async ({ browser, page }) => {
    expect(platformAdminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL').toBeTruthy()
    expect(platformAdminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD').toBeTruthy()
    expect(platformManagerPassword, 'TRYKATCH_ACCEPTANCE_PLATFORM_MANAGER_PASSWORD').toBeTruthy()
    expect(organizationAdminPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD').toBeTruthy()

    await test.step('the packaged React application signs in the bootstrap administrator', async () => {
      await page.goto('/login')
      await page.getByLabel('Email address').fill(platformAdminEmail!)
      await page.locator('input[name="password"]').fill(platformAdminPassword!)
      await page.getByRole('button', { name: 'Sign in', exact: true }).click()
      await expect(page).toHaveURL(/\/dashboard$/)
      await expect(page.getByRole('heading', { name: 'Platform overview' })).toBeVisible()
    })

    const platformRequest = page.context().request
    const platformSession = await getJson<SessionResponse>(platformRequest, '/api/v1/auth/session')

    await test.step('a platform administrator cannot suspend their own access', async () => {
      const response = await mutate(platformRequest, 'POST', `/api/v1/platform-users/${platformSession.userId}/suspend`)
      expect(response.status()).toBe(409)
      await expectProblem(response, 'self_change')
    })

    await test.step('a delegated access manager cannot suspend the final platform administrator', async () => {
      const roleResponse = await mutate(platformRequest, 'POST', '/api/v1/platform-users/roles', {
        name: 'Access manager',
        description: 'Manages platform access without holding platform-administrator status.',
        permissions: ['platform.users.read', 'platform.users.manage'],
      })
      expect(roleResponse.status()).toBe(201)
      const role = await roleResponse.json() as PlatformRoleResponse

      const grantResponse = await mutate(platformRequest, 'POST', '/api/v1/platform-users', {
        email: 'access.manager@example.test',
        displayName: 'Acceptance Access Manager',
        roleKey: role.key,
      })
      expect(grantResponse.status()).toBe(201)
      const grant = await grantResponse.json() as PlatformAccessGrantResponse
      expect(grant.activationToken).toBeTruthy()

      const managerContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
      try {
        expect((await mutate(managerContext.request, 'POST', '/api/v1/access-activation', {
          userId: grant.user.id,
          token: grant.activationToken,
          password: platformManagerPassword,
        })).status()).toBe(204)
        expect((await mutate(managerContext.request, 'POST', '/api/v1/auth/login', {
          email: grant.user.email,
          password: platformManagerPassword,
          rememberMe: false,
        })).status()).toBe(200)

        const response = await mutate(managerContext.request, 'POST', `/api/v1/platform-users/${platformSession.userId}/suspend`)
        expect(response.status()).toBe(409)
        await expectProblem(response, 'last_administrator')
      } finally {
        await managerContext.close()
      }
    })

    const first = await createOrganization(platformRequest, 'Acceptance Alpha', 'acceptance-alpha', 'alpha.admin@example.test')
    const second = await createOrganization(platformRequest, 'Acceptance Beta', 'acceptance-beta', 'beta.admin@example.test')

    const alphaContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const betaContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const replayContext = await browser.newContext({ baseURL: acceptanceBaseUrl })

    try {
      await activateInvitation(alphaContext.request, first.invitationToken, 'Alpha', 'Administrator')
      await activateInvitation(betaContext.request, second.invitationToken, 'Beta', 'Administrator')

      const project = await test.step('an organization administrator can create a project', async () => {
        const response = await mutate(alphaContext.request, 'POST', '/api/v1/projects', {
          name: 'Tenant isolation proof',
          description: 'Created by the generated-application acceptance journey.',
        })
        expect(response.status()).toBe(200)
        return await response.json() as ProjectResponse
      })

      await test.step('another organization cannot read the project', async () => {
        const response = await betaContext.request.get(`/api/v1/projects/${project.id}`)
        expect(response.status()).toBe(404)
      })

      await test.step('recoverable lifecycle and audit behavior work through public endpoints', async () => {
        expect((await mutate(alphaContext.request, 'POST', `/api/v1/projects/${project.id}/archive`)).status()).toBe(204)

        const archived = await getJson<PagedResponse<ProjectResponse>>(
          alphaContext.request,
          '/api/v1/projects?lifecycle=archived',
        )
        expect(archived.items.map(item => item.id)).toContain(project.id)

        expect((await mutate(alphaContext.request, 'POST', `/api/v1/projects/${project.id}/restore`)).status()).toBe(204)
        const audit = await getJson<AuditResponse>(alphaContext.request, '/api/v1/audit')
        expect(audit.items.some(item => item.action === 'project.created')).toBe(true)
        expect(audit.items.some(item => item.action === 'project.archived')).toBe(true)
        expect(audit.items.some(item => item.action === 'project.restored')).toBe(true)
      })

      await test.step('organization roles support permission-aware CRUD and recovery', async () => {
        const createResponse = await mutate(alphaContext.request, 'POST', '/api/v1/roles', {
          id: null,
          name: 'Project reviewer',
          description: 'Reads projects without changing them.',
          permissions: ['projects.read'],
        })
        expect(createResponse.status()).toBe(200)
        const role = await createResponse.json() as RoleResponse

        const updateResponse = await mutate(alphaContext.request, 'PUT', `/api/v1/roles/${role.id}`, {
          id: role.id,
          name: 'Project and audit reviewer',
          description: 'Reads projects and organization audit history.',
          permissions: ['projects.read', 'audit.read'],
        })
        expect(updateResponse.status()).toBe(200)

        expect((await mutate(alphaContext.request, 'POST', `/api/v1/roles/${role.id}/archive`)).status()).toBe(204)
        expect((await mutate(alphaContext.request, 'POST', `/api/v1/roles/${role.id}/restore`)).status()).toBe(204)
        expect((await mutate(alphaContext.request, 'POST', `/api/v1/roles/${role.id}/archive`)).status()).toBe(204)
        expect((await mutate(alphaContext.request, 'DELETE', `/api/v1/roles/${role.id}`, {
          reason: 'Acceptance lifecycle verification.',
        })).status()).toBe(204)

        const deletedRoles = await getJson<RoleResponse[]>(alphaContext.request, '/api/v1/roles?lifecycle=deleted')
        expect(deletedRoles.map(item => item.id)).toContain(role.id)
      })

      await test.step('an invitation is single-use', async () => {
        const response = await mutate(replayContext.request, 'POST', '/api/v1/invitations/activate', {
          token: first.invitationToken,
          firstName: 'Replay',
          lastName: 'Attempt',
          password: organizationAdminPassword,
        })
        expect(response.status()).toBe(400)
      })

      await test.step('the generated module catalog is available to an authenticated user', async () => {
        const modules = await getJson<Array<{ id: string }>>(alphaContext.request, '/api/v1/modules')
        expect(modules.map(module => module.id)).toContain('projects')
      })
    } finally {
      await Promise.all([alphaContext.close(), betaContext.close(), replayContext.close()])
    }
  })
})

async function createOrganization(
  request: APIRequestContext,
  name: string,
  slug: string,
  administratorEmail: string,
) {
  const response = await mutate(request, 'POST', '/api/v1/tenants', { name, slug, administratorEmail })
  expect(response.status()).toBe(201)
  return await response.json() as CreateOrganizationResponse
}

async function activateInvitation(
  request: APIRequestContext,
  token: string,
  firstName: string,
  lastName: string,
) {
  const response = await mutate(request, 'POST', '/api/v1/invitations/activate', {
    token,
    firstName,
    lastName,
    password: organizationAdminPassword,
  })
  expect(response.status()).toBe(200)
}

async function mutate(
  request: APIRequestContext,
  method: 'POST' | 'PUT' | 'DELETE',
  path: string,
  data?: unknown,
) {
  const antiforgery = await getJson<{ token: string }>(request, '/api/v1/auth/antiforgery')
  return request.fetch(path, {
    method,
    data,
    headers: { 'X-CSRF-TOKEN': antiforgery.token },
  })
}

async function getJson<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(path)
  expect(response.ok(), `${path} returned ${response.status()}`).toBe(true)
  return await response.json() as T
}

async function expectProblem(response: APIResponse, title: string) {
  const problem = await response.json() as { title?: string }
  expect(problem.title).toBe(title)
}

interface SessionResponse {
  userId: string
}

interface PlatformRoleResponse {
  key: string
}

interface PlatformAccessGrantResponse {
  user: {
    id: string
    email: string
  }
  activationToken: string | null
}

interface CreateOrganizationResponse {
  invitationToken: string
}

interface ProjectResponse {
  id: string
}

interface RoleResponse {
  id: string
}

interface PagedResponse<T> {
  items: T[]
}

interface AuditResponse {
  items: Array<{ action: string }>
}
