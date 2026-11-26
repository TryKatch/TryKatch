import { expect, test, type APIRequestContext, type APIResponse, type Locator, type Page, type Request, type Response, type Route } from '@playwright/test'
import { Buffer } from 'node:buffer'
import { access, mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, isAbsolute, join } from 'node:path'

declare const process: { env: Record<string, string | undefined> }

const enabled = process.env.TRYKATCH_ACCEPTANCE_FEATURES === 'true'
const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const platformAdminEmail = process.env.TRYKATCH_ACCEPTANCE_ADMIN_EMAIL
const platformAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD
const organizationAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD
const stage = process.env.TRYKATCH_ACCEPTANCE_FEATURE_STAGE
const statePath = process.env.TRYKATCH_ACCEPTANCE_FEATURE_STATE
const controlDirectory = process.env.TRYKATCH_ACCEPTANCE_FEATURE_CONTROL_DIR

test.describe('packaged application feature policy acceptance', () => {
  test('controls, live UI enforcement, and policy-store outage recovery', async ({ browser }) => {
    test.skip(!enabled || stage !== 'controls' || !acceptanceBaseUrl, 'Runs only for the controls phase against the feature acceptance host.')
    test.setTimeout(900_000)
    const adminEmail = required(platformAdminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL')
    const adminPassword = required(platformAdminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD')
    const ownerPassword = required(organizationAdminPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD')
    const outputState = absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_FEATURE_STATE')
    const markers = absolutePath(controlDirectory, 'TRYKATCH_ACCEPTANCE_FEATURE_CONTROL_DIR')

    const runId = `${Date.now()}-${test.info().workerIndex}`
    const ownerEmail = `feature-owner-${runId}@example.test`
    const platformContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const ownerContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const alphaActivationContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    let alphaId = ''
    let betaId = ''
    let project: ProjectResponse | undefined

    try {
      const adminPage = await platformContext.newPage()
      await signIn(adminPage, adminEmail, adminPassword)
      const alpha = await createOrganization(platformContext.request, `Feature Alpha ${runId}`, `feature-alpha-${runId}`, ownerEmail)
      const beta = await createOrganization(platformContext.request, `Feature Beta ${runId}`, `feature-beta-${runId}`, ownerEmail)
      alphaId = alpha.organization.id
      betaId = beta.organization.id

      await test.step('the organization record links to its scoped feature controls', async () => {
        await adminPage.goto('/dashboard/tenants')
        await adminPage.getByRole('searchbox', { name: 'Search tenants…' }).fill(`Feature Alpha ${runId}`)
        const alphaRow = adminPage.getByRole('row').filter({ hasText: `Feature Alpha ${runId}` })
        await alphaRow.getByRole('button', { name: `Actions for Feature Alpha ${runId}` }).click()
        await adminPage.getByRole('menuitem', { name: 'View' }).click()
        const details = adminPage.getByRole('dialog', { name: `Feature Alpha ${runId}` })
        await details.getByRole('link', { name: 'Feature controls' }).click()
        await expect(adminPage).toHaveURL(new RegExp(`/dashboard/features\\?organizationId=${alphaId}`))
      })

      await activateInvitation(alphaActivationContext.request, alpha.invitationToken, 'Feature', 'Owner', ownerPassword)
      await alphaActivationContext.close()

      const ownerPage = await ownerContext.newPage()
      await signIn(ownerPage, ownerEmail, ownerPassword)
      expect((await mutate(ownerContext.request, 'POST', '/api/v1/invitations/accept', { token: beta.invitationToken })).status()).toBe(200)
      await selectWorkspace(ownerContext.request, alphaId)
      await ownerPage.reload()

      await test.step('the real feature controls UI records inheritance, override, force-off precedence, and history', async () => {
        await setEnvironmentControl(adminPage, 'Project reads', true, false)
        await setEnvironmentControl(adminPage, 'Project reads', true, true)
        await expect(featureRow(adminPage, 'Project reads')).toContainText('Forced off')
        await setOrganizationControl(adminPage, alphaId, 'Project reads', true)
        const alphaOwnerReadsWhileForcedOff = await getJson<OrganizationFeatureSnapshot>(ownerContext.request, '/api/v1/features')
        expect(alphaOwnerReadsWhileForcedOff.values['projects.reads']).toBe(false)
        await expect(featureRow(adminPage, 'Project reads').getByRole('combobox', { name: 'Organization override for Project reads' })).toHaveText('Enabled')
        const blockedProjectRead = await requestWithRateLimit(() => ownerContext.request.get('/api/v1/projects?page=1&pageSize=100&lifecycle=active'))
        expect(blockedProjectRead.status()).toBe(409)
        await expectProblem(blockedProjectRead, 'feature_unavailable')
        await setEnvironmentControl(adminPage, 'Project reads', true, false)
        const alphaOwnerReadsAfterForceOff = await getJson<OrganizationFeatureSnapshot>(ownerContext.request, '/api/v1/features')
        expect(alphaOwnerReadsAfterForceOff.values['projects.reads']).toBe(true)
        expect((await requestWithRateLimit(() => ownerContext.request.get('/api/v1/projects?page=1&pageSize=100&lifecycle=active'))).status()).toBe(200)
        await setOrganizationControl(adminPage, alphaId, 'Project reads', false)
        await expect(featureRow(adminPage, 'Project reads')).toContainText('Disabled')
        await setOrganizationControl(adminPage, alphaId, 'Project reads', null)
        await expect(featureRow(adminPage, 'Project reads')).toContainText('Enabled')
        await expect(featureRow(adminPage, 'Project reads')).toContainText('Environment control')
        await setOrganizationControl(adminPage, betaId, 'Project reads', false)

        await adminPage.goto(`/dashboard/features?organizationId=${encodeURIComponent(alphaId)}`)
        await expect(adminPage.getByRole('heading', { name: 'Feature controls', exact: true })).toBeVisible()
        await adminPage.getByRole('button', { name: 'View history for Project reads' }).click()
        const history = adminPage.getByRole('dialog', { name: 'Feature history: Project reads' })
        await expect(history.getByRole('table', { name: 'Feature history' })).toBeVisible()
        await expect(history.locator('tbody tr').first()).toBeVisible()
        await adminPage.keyboard.press('Escape')
      })

      await test.step('a Documents upload survives a held real workspace refresh without submitting stale authority', async () => {
        await ownerPage.goto('/documents')
        await ownerPage.getByRole('button', { name: 'Upload document', exact: true }).first().click()
        const dialog = ownerPage.getByRole('dialog', { name: 'Upload document' })
        const title = `Retained upload draft ${runId}`
        const description = 'Typed before the real workspace response is held.'
        const bytes = Buffer.from('Document bytes retained across a workspace authority refresh.')
        await dialog.getByLabel('File', { exact: true }).setInputFiles({ name: 'refresh-notes.txt', mimeType: 'text/plain', buffer: bytes })
        await dialog.getByRole('textbox', { name: 'Document title' }).fill(title)
        await dialog.getByRole('textbox', { name: 'Description' }).fill(description)
        const staleForm = await dialog.locator('form').elementHandle()
        expect(staleForm).toBeTruthy()
        const uploads: Request[] = []
        const observeUpload = (request: Request) => {
          if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/documents/') uploads.push(request)
        }
        ownerPage.on('request', observeUpload)
        const held = await holdFirstOrganizationResponse<CurrentWorkspaceResponse>(ownerPage, '/api/v1/workspace/current', alphaId)
        try {
          const refresh = refreshCurrentWorkspace(ownerPage, alphaId)
          expect((await held.ready).organizationId).toBe(alphaId)
          await expect(dialog).toHaveCount(0)
          await staleForm!.evaluate((form) => (form as HTMLFormElement).requestSubmit())
          expect(uploads).toHaveLength(0)
          held.release()
          await held.fulfilled
          await refresh
          await expect(dialog).toBeVisible()
          await expect(dialog.getByRole('textbox', { name: 'Document title' })).toHaveValue(title)
          await expect(dialog.getByRole('textbox', { name: 'Description' })).toHaveValue(description)
          await expect(dialog.getByText('refresh-notes.txt', { exact: true })).toBeVisible()
          const uploaded = ownerPage.waitForResponse((response) => response.request().method() === 'POST'
            && new URL(response.url()).pathname === '/api/v1/documents/')
          await dialog.getByRole('button', { name: 'Upload document', exact: true }).click()
          const response = await uploaded
          expect(response.status()).toBe(201)
          const document = await response.json() as { id: string; title: string; description: string }
          expect(document).toMatchObject({ title, description })
          const content = await requestWithRateLimit(() => ownerContext.request.get(`/api/v1/documents/${encodeURIComponent(document.id)}/content`))
          expect(content.status()).toBe(200)
          expect(await content.body()).toEqual(bytes)
          expect(uploads).toHaveLength(1)
        } finally {
          held.release()
          await held.dispose()
          ownerPage.off('request', observeUpload)
        }
      })

      await test.step('the owner creates and retains an active project under the initial enabled read policy', async () => {
        const response = await mutate(ownerContext.request, 'POST', '/api/v1/projects', {
          name: `Retained feature project ${runId}`,
          description: 'Persisted by the packaged feature policy acceptance flow.',
        })
        expect(response.status()).toBe(201)
        project = await response.json() as ProjectResponse
        await ownerPage.goto('/projects')
        await expect(ownerPage.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()
        await expect(ownerPage.getByRole('row', { name: new RegExp(project.name) })).toBeVisible()
      })

      await test.step('a stale open create form is rejected by the real API after writes are disabled', async () => {
        await ownerPage.getByRole('button', { name: 'New project' }).click()
        const dialog = ownerPage.getByRole('dialog', { name: 'Create project' })
        await dialog.getByRole('textbox', { name: 'Name' }).fill(`Rejected stale write ${runId}`)
        await dialog.getByRole('textbox', { name: 'Description' }).fill('Must remain absent after the write control changes.')
        await refreshFeatureSnapshot(ownerPage)
        await expect(dialog).toBeVisible()
        await expect(dialog.getByRole('textbox', { name: 'Name' })).toHaveValue(`Rejected stale write ${runId}`)
        await expect(dialog.getByRole('textbox', { name: 'Description' })).toHaveValue('Must remain absent after the write control changes.')
        const staleForm = await dialog.locator('form').elementHandle()
        expect(staleForm).toBeTruthy()

        await setOrganizationControl(adminPage, alphaId, 'Project writes', false)
        await expect(dialog).toBeVisible()
        expect(await staleForm!.evaluate((form) => (form as HTMLFormElement).isConnected)).toBe(true)
        const rejectedRequest = ownerPage.waitForResponse((response) => {
          const url = new URL(response.url())
          return url.pathname === '/api/v1/projects' && response.request().method() === 'POST'
        })
        await staleForm!.evaluate((form) => (form as HTMLFormElement).requestSubmit())
        const rejected = await rejectedRequest
        expect(rejected.status()).toBe(409)
        await expectProblem(rejected, 'feature_unavailable')
        const activeProjects = await getJson<PagedResponse<ProjectResponse>>(ownerContext.request, '/api/v1/projects?page=1&pageSize=100&lifecycle=active')
        expect(activeProjects.items.some((item) => item.name === `Rejected stale write ${runId}`)).toBe(false)
      })

      await test.step('writes-off leaves reads and View available but removes New, Edit, and Archive', async () => {
        await refreshFeatureSnapshot(ownerPage)
        await expect(ownerPage.getByRole('row', { name: new RegExp(project!.name) })).toBeVisible()
        await expect(ownerPage.getByRole('button', { name: 'New project' })).toHaveCount(0)
        await ownerPage.getByRole('button', { name: `Actions for ${project!.name}` }).click()
        await expect(ownerPage.getByRole('menuitem', { name: 'View' })).toBeVisible()
        await expect(ownerPage.getByRole('menuitem', { name: 'Edit' })).toHaveCount(0)
        await expect(ownerPage.getByRole('menuitem', { name: 'Archive' })).toHaveCount(0)
        await ownerPage.keyboard.press('Escape')
      })

      await test.step('reads-off removes cached project content and nav, then a fresh enable restores it', async () => {
        await setOrganizationControl(adminPage, alphaId, 'Project reads', false)
        await refreshFeatureSnapshot(ownerPage)
        await expect(ownerPage.getByRole('heading', { name: 'Feature unavailable' })).toBeVisible()
        await expect(ownerPage.getByRole('link', { name: 'Projects', exact: true })).toHaveCount(0)
        await expect(ownerPage.getByText(project!.name)).toHaveCount(0)

        await setOrganizationControl(adminPage, alphaId, 'Project reads', null)
        await refreshFeatureSnapshot(ownerPage)
        await expect(ownerPage.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()
        await expect(ownerPage.getByRole('row', { name: new RegExp(project!.name) })).toBeVisible()
      })

      await test.step('a mounted workspace switch rejects a late real Alpha feature response', async () => {
        const heldAlpha = await holdFirstFeatureResponse(ownerPage, alphaId)
        await ownerPage.evaluate(() => window.dispatchEvent(new Event('focus')))
        const staleAlpha = await heldAlpha.ready
        expect(staleAlpha.organizationId).toBe(alphaId)
        expect(staleAlpha.values['projects.reads']).toBe(true)

        const betaFeatureResponse = ownerPage.waitForResponse((response) => {
          return new URL(response.url()).pathname === '/api/v1/features' && response.status() === 200
        }, { timeout: 60_000 })
        await selectWorkspace(ownerContext.request, betaId)
        await refreshCurrentWorkspace(ownerPage, betaId)
        const currentWorkspace = await getJson<CurrentWorkspaceResponse>(ownerContext.request, '/api/v1/workspace/current')
        expect(currentWorkspace.organizationId).toBe(betaId)
        const betaSnapshot = await betaFeatureResponse
        const betaFeatures = await betaSnapshot.json() as OrganizationFeatureSnapshot
        expect(betaFeatures.organizationId).toBe(betaId)
        expect(betaFeatures.values['projects.reads']).toBe(false)
        await expect(ownerPage.getByRole('heading', { name: 'Feature unavailable' })).toBeVisible()
        await expect(ownerPage.getByRole('link', { name: 'Projects', exact: true })).toHaveCount(0)
        await expect(ownerPage.getByText(project!.name)).toHaveCount(0)

        heldAlpha.release()
        await heldAlpha.fulfilled
        await expect(ownerPage.getByRole('heading', { name: 'Feature unavailable' })).toBeVisible()
        await expect(ownerPage.getByRole('link', { name: 'Projects', exact: true })).toHaveCount(0)
        await expect(ownerPage.getByText(project!.name)).toHaveCount(0)
        await heldAlpha.dispose()

        await selectWorkspace(ownerContext.request, alphaId)
        await refreshCurrentWorkspace(ownerPage, alphaId)
        await expect(ownerPage.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()
        await expect(ownerPage.getByRole('row', { name: new RegExp(project!.name) })).toBeVisible()
      })

      // Retain an explicit Alpha read override so the rebuilt-host stages can
      // prove that both organization control values and their history survive.
      await setOrganizationControl(adminPage, alphaId, 'Project reads', true)

      const retained = await readRetainedControls(platformContext.request, alphaId, betaId)
      expect(retained.environmentReads.enabled).toBe(true)
      expect(retained.environmentReads.forceDisabled).toBe(false)
      expect(retained.alphaReads.enabled).toBe(true)
      expect(retained.alphaWrites.enabled).toBe(false)
      expect(retained.betaReads.enabled).toBe(false)
      const state: FeatureAcceptanceState = {
        ownerEmail,
        organizationId: alphaId,
        secondOrganizationId: betaId,
        projectId: project!.id,
        projectName: project!.name,
        controls: {
          environmentReadsVersion: retained.environmentReads.version,
          alphaReadsVersion: retained.alphaReads.version,
          alphaWritesVersion: retained.alphaWrites.version,
          betaReadsVersion: retained.betaReads.version,
        },
      }
      await writeState(outputState, state)

      await test.step('the same browser fails closed during an authoritative feature-store outage, then recovers', async () => {
        await writeMarker(markers, 'outage-ready')
        await waitForMarker(markers, 'policy-blocked', 120_000)

        const outageSnapshot = ownerPage.waitForResponse((response) => {
          const url = new URL(response.url())
          return url.pathname === '/api/v1/features' && response.status() === 503
        }, { timeout: 120_000 })
        await ownerPage.evaluate(() => window.dispatchEvent(new Event('focus')))
        const failedSnapshot = await outageSnapshot
        expect(failedSnapshot.status()).toBe(503)
        await expectProblem(failedSnapshot, 'feature_policy_unavailable')
        await expect(ownerPage.getByRole('heading', { name: 'Feature unavailable' })).toBeVisible()
        await expect(ownerPage.getByRole('link', { name: 'Projects', exact: true })).toHaveCount(0)
        await expect(ownerPage.getByText(project!.name)).toHaveCount(0)

        const blockedWrite = await mutate(ownerContext.request, 'POST', '/api/v1/projects', {
          name: `Blocked during outage ${runId}`,
          description: 'The authoritative feature policy store is unavailable.',
        })
        expect(blockedWrite.status()).toBe(503)
        await expectProblem(blockedWrite, 'feature_policy_unavailable')
        await writeMarker(markers, 'restore-requested')
        await waitForMarker(markers, 'policy-restored', 120_000)

        const recoverySnapshot = ownerPage.waitForResponse((response) => {
          const url = new URL(response.url())
          return url.pathname === '/api/v1/features' && response.status() === 200
        }, { timeout: 120_000 })
        await ownerPage.evaluate(() => window.dispatchEvent(new Event('focus')))
        const recovered = await recoverySnapshot
        const recoveredFeatures = await recovered.json() as OrganizationFeatureSnapshot
        expect(recoveredFeatures.organizationId).toBe(alphaId)
        expect(recoveredFeatures.values['projects.reads']).toBe(true)
        expect(recoveredFeatures.values['projects.writes']).toBe(false)
        await expect(ownerPage.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()
        await expect(ownerPage.getByRole('row', { name: new RegExp(project!.name) })).toBeVisible()
        await expect(ownerPage.getByRole('button', { name: 'New project' })).toHaveCount(0)
        const afterOutage = await getJson<PagedResponse<ProjectResponse>>(ownerContext.request, '/api/v1/projects?page=1&pageSize=100&lifecycle=active')
        expect(afterOutage.items.some((item) => item.name === `Blocked during outage ${runId}`)).toBe(false)
      })
    } finally {
      await Promise.all([platformContext.close(), ownerContext.close(), alphaActivationContext.close()])
    }
  })

  test('an absent module has no installed feature keys or active feature history', async ({ browser }) => {
    test.skip(!enabled || stage !== 'absent' || !acceptanceBaseUrl, 'Runs only for the module-absence phase against the feature acceptance host.')
    test.setTimeout(180_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_FEATURE_STATE'))
    const admin = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const owner = await browser.newContext({ baseURL: acceptanceBaseUrl })
    try {
      const adminPage = await admin.newPage()
      await signIn(adminPage, required(platformAdminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL'), required(platformAdminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD'))
      await adminPage.goto('/dashboard/features')
      await expect(adminPage.getByRole('heading', { name: 'Feature controls', exact: true })).toBeVisible()
      await expect(adminPage.getByText('Project reads')).toHaveCount(0)
      await expect(adminPage.getByText('Project writes')).toHaveCount(0)

      const controls = await getJson<FeatureAdministrationSnapshot>(admin.request, '/api/v1/platform/features')
      expect(controls.items.some((item) => item.declaration.key.startsWith('projects.'))).toBe(false)
      const history = await requestWithRateLimit(() => admin.request.get(`/api/v1/platform/features/projects.reads/history?organizationId=${encodeURIComponent(state.organizationId)}`))
      expect(history.status()).toBe(404)
      const modules = await getJson<Array<{ id: string }>>(admin.request, '/api/v1/modules')
      expect(modules.map((module) => module.id)).not.toContain('projects')

      const ownerPage = await owner.newPage()
      await signIn(ownerPage, state.ownerEmail, required(organizationAdminPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD'))
      await selectWorkspace(owner.request, state.organizationId)
      await ownerPage.reload()
      await expect(ownerPage.getByRole('link', { name: 'Projects', exact: true })).toHaveCount(0)
      const projects = await requestWithRateLimit(() => owner.request.get('/api/v1/projects?page=1&pageSize=100&lifecycle=active'))
      expect(projects.status()).toBe(404)
      await ownerPage.goto('/projects')
      await expect(ownerPage.getByRole('heading', { name: 'Projects', exact: true })).toHaveCount(0)
    } finally {
      await Promise.all([admin.close(), owner.close()])
    }
  })

  test('restored modules retain organization controls, history, and existing project data', async ({ browser }) => {
    test.skip(!enabled || stage !== 'restored' || !acceptanceBaseUrl, 'Runs only for the restored-module phase against the feature acceptance host.')
    test.setTimeout(180_000)
    const state = await readState(absolutePath(statePath, 'TRYKATCH_ACCEPTANCE_FEATURE_STATE'))
    const admin = await browser.newContext({ baseURL: acceptanceBaseUrl })
    const owner = await browser.newContext({ baseURL: acceptanceBaseUrl })
    try {
      const adminPage = await admin.newPage()
      await signIn(adminPage, required(platformAdminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL'), required(platformAdminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD'))
      await adminPage.goto(`/dashboard/features?organizationId=${encodeURIComponent(state.organizationId)}`)
      await expect(adminPage.getByText('Project reads', { exact: true })).toBeVisible()
      await expect(adminPage.getByText('Project writes', { exact: true })).toBeVisible()
      const retained = await readRetainedControls(admin.request, state.organizationId, state.secondOrganizationId)
      expect(retained.environmentReads.version).toBe(state.controls.environmentReadsVersion)
      expect(retained.environmentReads.enabled).toBe(true)
      expect(retained.environmentReads.forceDisabled).toBe(false)
      expect(retained.alphaReads.version).toBe(state.controls.alphaReadsVersion)
      expect(retained.alphaReads.enabled).toBe(true)
      expect(retained.alphaWrites.version).toBe(state.controls.alphaWritesVersion)
      expect(retained.alphaWrites.enabled).toBe(false)
      expect(retained.betaReads.version).toBe(state.controls.betaReadsVersion)
      expect(retained.betaReads.enabled).toBe(false)

      await adminPage.getByRole('button', { name: 'View history for Project reads' }).click()
      const history = adminPage.getByRole('dialog', { name: 'Feature history: Project reads' })
      await expect(history.getByRole('table', { name: 'Feature history' })).toBeVisible()
      await expect(history.locator('tbody tr').first()).toBeVisible()

      const ownerPage = await owner.newPage()
      await signIn(ownerPage, state.ownerEmail, required(organizationAdminPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD'))
      await selectWorkspace(owner.request, state.organizationId)
      await ownerPage.reload()
      await ownerPage.goto('/projects')
      await expect(ownerPage.getByRole('link', { name: 'Projects', exact: true })).toBeVisible()
      await expect(ownerPage.getByRole('row', { name: new RegExp(state.projectName) })).toBeVisible()
      await expect(ownerPage.getByRole('button', { name: 'New project' })).toHaveCount(0)
      const project = await getJson<ProjectResponse>(owner.request, `/api/v1/projects/${state.projectId}`)
      expect(project.name).toBe(state.projectName)
    } finally {
      await Promise.all([admin.close(), owner.close()])
    }
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

async function activateInvitation(request: APIRequestContext, token: string, firstName: string, lastName: string, password: string) {
  const response = await mutate(request, 'POST', '/api/v1/invitations/activate', { token, firstName, lastName, password })
  expect(response.status()).toBe(200)
}

async function selectWorkspace(request: APIRequestContext, organizationId: string) {
  const response = await mutate(request, 'POST', '/api/v1/workspace/select', { organizationId, remember: true })
  expect(response.status()).toBe(204)
}

async function setEnvironmentControl(page: Page, featureName: string, enabledValue: boolean | null, forceDisabled: boolean) {
  await page.goto('/dashboard/features')
  await expect(page.getByRole('heading', { name: 'Feature controls', exact: true })).toBeVisible()
  const row = featureRow(page, featureName)
  const field = row.getByRole('combobox', { name: `Environment control for ${featureName}` })
  const desired = enabledValue === null ? 'Inherit configured default' : enabledValue ? 'Enabled' : 'Disabled'
  if ((await field.innerText()).trim() !== desired) await choose(page, field, desired)
  const force = row.getByRole('checkbox', { name: 'Force feature off' })
  if (await force.isChecked() !== forceDisabled) await force.setChecked(forceDisabled)
  const save = row.getByRole('button', { name: `Save environment control for ${featureName}` })
  if (await save.isEnabled()) {
    await save.click()
    await expect(page.getByText('Settings saved.', { exact: true })).toBeVisible()
  }
}

async function setOrganizationControl(page: Page, organizationId: string, featureName: string, enabledValue: boolean | null) {
  await page.goto(`/dashboard/features?organizationId=${encodeURIComponent(organizationId)}`)
  await expect(page.getByRole('heading', { name: 'Feature controls', exact: true })).toBeVisible()
  const row = featureRow(page, featureName)
  const field = row.getByRole('combobox', { name: `Organization override for ${featureName}` })
  const desired = enabledValue === null ? 'Inherit environment' : enabledValue ? 'Enabled' : 'Disabled'
  if ((await field.innerText()).trim() !== desired) await choose(page, field, desired)
  const save = row.getByRole('button', { name: `Save organization override for ${featureName}` })
  if (await save.isEnabled()) {
    await save.click()
    await expect(page.getByText('Settings saved.', { exact: true })).toBeVisible()
  }
}

function featureRow(page: Page, featureName: string) {
  return page.getByRole('row').filter({ hasText: featureName })
}

async function choose(page: Page, field: Locator, option: string) {
  await field.click()
  await page.getByRole('option', { name: option, exact: true }).click()
}

async function refreshFeatureSnapshot(page: Page) {
  const refreshed = page.waitForResponse((response) => {
    const url = new URL(response.url())
    return url.pathname === '/api/v1/features' && response.status() === 200
  }, { timeout: 60_000 })
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await refreshed
}

async function refreshCurrentWorkspace(page: Page, organizationId: string) {
  const eligible = new WeakSet<Request>()
  const completed: Request[] = []
  let workspaceResponse: Response | null = null
  const requested = (request: Request) => {
    if (request.method() === 'GET'
      && new URL(request.url()).pathname === '/api/v1/workspace/current'
      && request.frame() === page.mainFrame()) eligible.add(request)
  }
  const finished = (request: Request) => { if (eligible.has(request)) completed.push(request) }
  page.on('request', requested)
  page.on('requestfinished', finished)
  try {
    await page.bringToFront()
    await expect.poll(async () => {
      while (completed.length) {
        const response = await completed.shift()!.response()
        if (response?.status() === 429) continue
        workspaceResponse = response
        return true
      }
      // The application refreshes its mounted workspace query after visibility changes.
      await page.evaluate(() => window.dispatchEvent(new Event('visibilitychange')))
      return false
    }, { timeout: 65_000, intervals: [100, 250, 500, 1_000], message: 'the mounted document should refresh its current workspace' }).toBe(true)
    expect(workspaceResponse, 'the current workspace request should receive a response').toBeTruthy()
    expect(workspaceResponse!.ok(), 'the current workspace request should succeed').toBe(true)
    expect((await workspaceResponse!.json() as CurrentWorkspaceResponse).organizationId,
      'the mounted workspace must match the selected organization').toBe(organizationId)
  } finally {
    page.off('request', requested)
    page.off('requestfinished', finished)
  }
}

async function holdFirstFeatureResponse(page: Page, organizationId: string) {
  return holdFirstOrganizationResponse<OrganizationFeatureSnapshot>(page, '/api/v1/features', organizationId)
}

async function holdFirstOrganizationResponse<T extends { organizationId: string }>(page: Page, path: string, organizationId: string) {
  let resolveReady!: (snapshot: T) => void
  let releaseResponse!: () => void
  let resolveFulfilled!: () => void
  let hasHeldResponse = false
  const ready = new Promise<T>((resolve) => { resolveReady = resolve })
  const releaseGate = new Promise<void>((resolve) => { releaseResponse = resolve })
  const fulfilled = new Promise<void>((resolve) => { resolveFulfilled = resolve })
  const handler = async (route: Route) => {
    const response = await route.fetch()
    const snapshot = await response.json() as T
    if (!hasHeldResponse && snapshot.organizationId === organizationId && response.status() === 200) {
      hasHeldResponse = true
      resolveReady(snapshot)
      await releaseGate
      try { await route.fulfill({ response }) }
      finally { resolveFulfilled() }
      return
    }
    await route.fulfill({ response })
  }
  await page.route(`**${path}`, handler)
  return {
    ready,
    fulfilled,
    release: releaseResponse,
    dispose: () => page.unroute(`**${path}`, handler),
  }
}

async function readRetainedControls(request: APIRequestContext, alphaId: string, betaId: string) {
  const [environment, alpha, beta] = await Promise.all([
    getJson<FeatureAdministrationSnapshot>(request, '/api/v1/platform/features'),
    getJson<FeatureAdministrationSnapshot>(request, `/api/v1/platform/features?organizationId=${encodeURIComponent(alphaId)}`),
    getJson<FeatureAdministrationSnapshot>(request, `/api/v1/platform/features?organizationId=${encodeURIComponent(betaId)}`),
  ])
  return {
    environmentReads: featureItem(environment, 'projects.reads').environmentControl,
    alphaReads: featureItem(alpha, 'projects.reads').organizationControl!,
    alphaWrites: featureItem(alpha, 'projects.writes').organizationControl!,
    betaReads: featureItem(beta, 'projects.reads').organizationControl!,
  }
}

function featureItem(snapshot: FeatureAdministrationSnapshot, key: string) {
  const item = snapshot.items.find((candidate) => candidate.declaration.key === key)
  expect(item, `the actual feature catalog should contain ${key}`).toBeTruthy()
  return item!
}

async function mutate(request: APIRequestContext, method: 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown) {
  const antiforgery = await getJson<{ token: string }>(request, '/api/v1/auth/antiforgery')
  return requestWithRateLimit(() => request.fetch(path, {
    method,
    data,
    headers: { 'X-CSRF-TOKEN': antiforgery.token },
  }))
}

async function getJson<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await requestWithRateLimit(() => request.get(path))
  expect(response.ok(), `${path} returned ${response.status()}`).toBe(true)
  return await response.json() as T
}

async function requestWithRateLimit(operation: () => Promise<APIResponse>) {
  const deadline = Date.now() + 65_000
  while (true) {
    const response = await operation()
    if (response.status() !== 429) return response
    if (Date.now() >= deadline) throw new Error('Public request remained rate limited beyond one request window.')
    await new Promise((resolve) => setTimeout(resolve, 2_000))
  }
}

async function expectProblem(response: Pick<APIResponse, 'json'>, title: string) {
  const problem = await response.json() as { title?: string }
  expect(problem.title).toBe(title)
  expect(JSON.stringify(problem)).not.toMatch(/select|exception|stack|database|connection string/i)
}

async function writeState(path: string, state: FeatureAcceptanceState) {
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, `${JSON.stringify(state, null, 2)}\n`, { mode: 0o600 })
}

async function readState(path: string): Promise<FeatureAcceptanceState> {
  const parsed = JSON.parse(await readFile(path, 'utf8')) as FeatureAcceptanceState
  expect(parsed.ownerEmail).toMatch(/^[^@\s]+@example\.test$/)
  expect(parsed.organizationId).toMatch(/^[0-9a-f-]{36}$/i)
  expect(parsed.secondOrganizationId).toMatch(/^[0-9a-f-]{36}$/i)
  expect(parsed.projectId).toMatch(/^[0-9a-f-]{36}$/i)
  expect(parsed.projectName).toContain('Retained feature project')
  return parsed
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
  }, { timeout, intervals: [100, 250, 500, 1_000], message: `the acceptance harness should create ${marker}` }).toBe(true)
}

function required(value: string | undefined, name: string): string {
  expect(value, name).toBeTruthy()
  return value!
}

function absolutePath(value: string | undefined, name: string): string {
  const path = required(value, name)
  expect(isAbsolute(path), `${name} must be an absolute path`).toBe(true)
  return path
}

interface CreateOrganizationResponse { organization: { id: string }; invitationToken: string }
interface ProjectResponse { id: string; name: string }
interface PagedResponse<T> { items: T[] }
interface CurrentWorkspaceResponse { organizationId: string }
interface OrganizationFeatureSnapshot { organizationId: string; values: Record<string, boolean> }
interface FeatureControl { enabled: boolean | null; forceDisabled: boolean; version: string }
interface FeatureItem { declaration: { key: string }; environmentControl: FeatureControl; organizationControl: FeatureControl | null }
interface FeatureAdministrationSnapshot { items: FeatureItem[] }
interface FeatureAcceptanceState {
  ownerEmail: string
  organizationId: string
  secondOrganizationId: string
  projectId: string
  projectName: string
  controls: {
    environmentReadsVersion: string
    alphaReadsVersion: string
    alphaWritesVersion: string
    betaReadsVersion: string
  }
}
