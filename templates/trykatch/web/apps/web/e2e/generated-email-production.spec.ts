import { randomUUID } from 'node:crypto'
import { chmod, readFile, writeFile } from 'node:fs/promises'
import { isAbsolute } from 'node:path'
import { expect, request as playwrightRequest, test, type APIRequestContext, type Page } from '@playwright/test'

const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const adminEmail = process.env.TRYKATCH_ACCEPTANCE_ADMIN_EMAIL
const adminPassword = process.env.TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD
const organizationPassword = process.env.TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD
const operatorEmail = 'e5.email.operator@example.test'
const operatorPassword = process.env.TRYKATCH_ACCEPTANCE_EMAIL_OPERATOR_PASSWORD
const inboxUrl = process.env.TRYKATCH_ACCEPTANCE_INBOX_URL
const statePath = process.env.TRYKATCH_ACCEPTANCE_EMAIL_STATE
const emailAcceptanceEnabled = process.env.TRYKATCH_ACCEPTANCE_EMAIL === 'true'
const stage = process.env.TRYKATCH_ACCEPTANCE_EMAIL_STAGE

test.use({ trace: 'off' })
test.skip(!emailAcceptanceEnabled, 'Runs only when the generated email production acceptance is explicitly enabled.')

test(stage === 'prepare' ? 'email production prepare queues invitation and reset work' : 'email production resume delivers the persisted invitation and reset work', async ({ browser, page }) => {
  test.setTimeout(300_000)
  expect(acceptanceBaseUrl, 'TRYKATCH_ACCEPTANCE_BASE_URL').toBeTruthy()
  expect(adminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL').toBeTruthy()
  expect(adminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD').toBeTruthy()
  expect(organizationPassword, 'TRYKATCH_ACCEPTANCE_ORGANIZATION_PASSWORD').toBeTruthy()
  expect(operatorPassword, 'TRYKATCH_ACCEPTANCE_EMAIL_OPERATOR_PASSWORD').toBeTruthy()
  expect(statePath, 'TRYKATCH_ACCEPTANCE_EMAIL_STATE').toBeTruthy()
  expect(isAbsolute(statePath!), 'The email acceptance state path must be absolute.').toBe(true)

  if (stage === 'prepare') {
    await prepareEmailAcceptance(browser, page)
    return
  }

  if (stage === 'resume') {
    expect(inboxUrl, 'TRYKATCH_ACCEPTANCE_INBOX_URL').toBeTruthy()
    await resumeEmailAcceptance(browser, page)
    return
  }

  throw new Error('TRYKATCH_ACCEPTANCE_EMAIL_STAGE must be prepare or resume.')
})

async function prepareEmailAcceptance(browser: import('@playwright/test').Browser, page: Page) {
  const marker = randomUUID().replaceAll('-', '').slice(0, 16)
  const ownerEmail = `e5.owner.${marker}@example.test`
  const inviteRecipient = `e5.invitee.${marker}@example.test`
  const adminRequest = page.context().request

  await signInThroughUi(page, adminEmail!, adminPassword!)
  await expect(page.getByRole('link', { name: 'Email delivery', exact: true })).toHaveCount(0)
  expect((await adminRequest.get('/api/v1/platform/email-deliveries?boundary=organization')).status()).toBe(403)

  const roleResponse = await mutate(adminRequest, 'POST', '/api/v1/platform-users/roles', {
    name: `Email delivery reader ${marker}`,
    description: 'Read-only email delivery inspection for production acceptance.',
    permissions: ['platform.dashboard.read', 'platform.email.read'],
  })
  expect(roleResponse.status()).toBe(201)
  const role = await roleResponse.json() as { key: string }

  const grantResponse = await mutate(adminRequest, 'POST', '/api/v1/platform-users', {
    email: operatorEmail,
    displayName: 'E5 Email Delivery Operator',
    roleKey: role.key,
  })
  expect(grantResponse.status()).toBe(201)
  const grant = await grantResponse.json() as { user: { id: string }; activationToken: string | null }
  expect(typeof grant.activationToken).toBe('string')

  const operatorActivation = await browser.newContext({ baseURL: acceptanceBaseUrl })
  try {
    const activated = await mutate(operatorActivation.request, 'POST', '/api/v1/access-activation', {
      userId: grant.user.id,
      token: grant.activationToken,
      password: operatorPassword,
    })
    expect(activated.status()).toBe(204)
  } finally {
    await operatorActivation.close()
  }

  const organizationResponse = await mutate(adminRequest, 'POST', '/api/v1/tenants', {
    name: `Email queue acceptance ${marker}`,
    slug: `email-queue-${marker}`,
    administratorEmail: ownerEmail,
  })
  expect(organizationResponse.status()).toBe(201)
  const organization = await organizationResponse.json() as { organization: { id: string }; invitationToken: string }

  const ownerContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
  let organizationInvitationUrl = ''
  let organizationInvitationId = ''
  try {
    const activated = await mutate(ownerContext.request, 'POST', '/api/v1/invitations/activate', {
      token: organization.invitationToken,
      firstName: 'Email',
      lastName: 'Owner',
      password: organizationPassword,
    })
    expect(activated.status()).toBe(200)

    const ownerPage = await ownerContext.newPage()
    await ownerPage.goto('/user-management')
    await expect(ownerPage.getByRole('heading', { name: 'User management' })).toBeVisible()
    await ownerPage.getByRole('button', { name: 'Invite person', exact: true }).click()
    const dialog = ownerPage.getByRole('dialog')
    await dialog.getByRole('textbox', { name: 'Email address' }).fill(inviteRecipient)
    const inviteResponsePromise = ownerPage.waitForResponse((response) =>
      response.url().includes('/api/v1/invitations') && response.request().method() === 'POST')
    await dialog.getByRole('button', { name: 'Create invitation', exact: true }).click()
    const inviteResponse = await inviteResponsePromise
    expect(inviteResponse.status()).toBe(200)
    const inviteReceipt = await inviteResponse.json() as {
      invitation: { id: string }
      invitationUrl: string
      emailDelivered: boolean
      emailQueued: boolean
    }
    organizationInvitationId = inviteReceipt.invitation.id
    organizationInvitationUrl = inviteReceipt.invitationUrl
    expect(inviteReceipt.emailQueued).toBe(true)
    expect(inviteReceipt.emailDelivered).toBe(false)
    await expect(dialog.getByText(/The invitation is queued for email delivery/)).toBeVisible()
    await expect(dialog.getByText(/Delivery has not been confirmed yet/)).toBeVisible()
    expect((await dialog.locator('code').innerText()) === organizationInvitationUrl).toBe(true)
  } finally {
    await ownerContext.close()
  }

  const resetRequestContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
  let eligibleResetReceipt: { message: string; deliveryConfigured: boolean; developmentResetUrl?: string | null }
  let unknownResetReceipt: { message: string; deliveryConfigured: boolean; developmentResetUrl?: string | null }
  const unknownEmail = `e5.unknown.${marker}@example.test`
  try {
    const resetResponse = await mutate(resetRequestContext.request, 'POST', '/api/v1/auth/password/forgot', { email: ownerEmail })
    expect(resetResponse.status()).toBe(202)
    eligibleResetReceipt = await resetResponse.json() as typeof eligibleResetReceipt

    const unknownResponse = await mutate(resetRequestContext.request, 'POST', '/api/v1/auth/password/forgot', { email: unknownEmail })
    expect(unknownResponse.status()).toBe(202)
    unknownResetReceipt = await unknownResponse.json() as typeof unknownResetReceipt
  } finally {
    await resetRequestContext.close()
  }
  expect(eligibleResetReceipt!.message).toBe('If an eligible account exists, password reset instructions are on the way.')
  expect(unknownResetReceipt!.message).toBe(eligibleResetReceipt!.message)
  expect(eligibleResetReceipt!.deliveryConfigured).toBe(true)
  expect(unknownResetReceipt!.deliveryConfigured).toBe(true)
  expect(eligibleResetReceipt!.developmentResetUrl ?? null).toBe(null)
  expect(unknownResetReceipt!.developmentResetUrl ?? null).toBe(null)

  const operatorContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
  try {
    const operatorPage = await operatorContext.newPage()
    await signInThroughUi(operatorPage, operatorEmail, operatorPassword!)
    await expect(operatorPage.getByRole('link', { name: 'Email delivery', exact: true })).toBeVisible()
    await operatorPage.getByRole('link', { name: 'Email delivery', exact: true }).click()
    await expect(operatorPage.getByRole('heading', { name: 'Email delivery', exact: true })).toBeVisible()

    const organizationRows = await readDeliveryPage(operatorPage.context().request, 'organization', organization.organization.id)
    const identityRows = await readDeliveryPage(operatorPage.context().request, 'identity')
    const organizationDelivery = singleDelivery(organizationRows, (row) => row.templateKey === 'core.organization-invitation' && row.organizationId === organization.organization.id)
    const passwordResetDelivery = singleDelivery(identityRows, (row) => row.templateKey === 'core.password-reset')
    for (const delivery of [organizationDelivery, passwordResetDelivery]) {
      expect(['queued', 'leased']).toContain(delivery.state)
      expect(delivery.attempts).toBeLessThan(5)
    }

    await expect(operatorPage.getByText(organizationDelivery.deliveryId)).toBeVisible()
    await operatorPage.getByRole('combobox', { name: 'Boundary' }).click()
    await operatorPage.getByRole('option', { name: 'Identity', exact: true }).click()
    await expect(operatorPage.getByText(passwordResetDelivery.deliveryId)).toBeVisible()

    assertSafeMetadata([organizationDelivery, passwordResetDelivery], [ownerEmail, inviteRecipient, organizationInvitationUrl])
    await writePrivateState({
      version: 1,
      organizationId: organization.organization.id,
      organizationInvitationId,
      organizationDeliveryId: organizationDelivery.deliveryId,
      identityDeliveryId: passwordResetDelivery.deliveryId,
      operatorEmail,
      ownerEmail,
      inviteRecipient,
      invitationUrl: organizationInvitationUrl,
      privateMarkers: [operatorEmail, ownerEmail, inviteRecipient, unknownEmail, organizationInvitationUrl],
    })
  } finally {
    await operatorContext.close()
  }
}

async function resumeEmailAcceptance(browser: import('@playwright/test').Browser, page: Page) {
  const state = await readPrivateState()
  // Shutdown may retain a full two-minute lease. Allow recovery, the next
  // poll and a complete bounded SMTP attempt without changing production leases.
  const deliveryDeadline = Date.now() + 210_000
  const mailbox = await playwrightRequest.newContext({ baseURL: inboxUrl })
  let resetUrl: URL | undefined
  let resetToken: string | undefined
  let inviteToken: string | undefined
  try {
    const messages = await waitForFixtureMessages(mailbox, [state.inviteRecipient, state.ownerEmail], deliveryDeadline)
    const inviteSummary = messages.find((message) => messageTo(message).includes(state.inviteRecipient))
    const resetSummary = messages.find((message) => messageTo(message).includes(state.ownerEmail))
    expect(Boolean(inviteSummary)).toBe(true)
    expect(Boolean(resetSummary)).toBe(true)
    expect(messages.length).toBe(2)

    const invitationMessage = await getJson<UnknownRecord>(mailbox, `/api/v1/message/${encodeURIComponent(recordId(inviteSummary!))}`)
    const resetMessage = await getJson<UnknownRecord>(mailbox, `/api/v1/message/${encodeURIComponent(recordId(resetSummary!))}`)
    expect(messageTo(invitationMessage).includes(state.inviteRecipient)).toBe(true)
    expect(messageTo(resetMessage).includes(state.ownerEmail)).toBe(true)

    const invitationText = bodyText(invitationMessage)
    const resetText = bodyText(resetMessage)
    expect(invitationText.includes('You have been invited to join')).toBe(true)
    expect(resetText.includes('Use this link to reset your password')).toBe(true)
    expect(invitationText.includes(state.invitationUrl)).toBe(true)

    const appOrigin = new URL(acceptanceBaseUrl!).origin
    const inviteUrl = findUrl(invitationText, '/invite/')
    expect(Boolean(inviteUrl)).toBe(true)
    if (inviteUrl) {
      expect(inviteUrl.origin === appOrigin && inviteUrl.pathname.startsWith('/invite/')).toBe(true)
      inviteToken = decodeURIComponent(inviteUrl.pathname.slice('/invite/'.length))
      const originalInvite = new URL(state.invitationUrl)
      expect(inviteToken === decodeURIComponent(originalInvite.pathname.slice('/invite/'.length))).toBe(true)
    }

    resetUrl = findUrl(resetText, '/reset-password')
    expect(Boolean(resetUrl)).toBe(true)
    resetToken = resetUrl?.searchParams.get('token') ?? undefined
    expect(Boolean(resetUrl && resetUrl.origin === appOrigin && resetUrl.pathname.endsWith('/reset-password'))).toBe(true)
    expect(resetUrl?.searchParams.get('email')?.toLowerCase() === state.ownerEmail.toLowerCase()).toBe(true)
    expect(Boolean(resetToken)).toBe(true)

    const updatedMarkers = [...new Set([
      ...state.privateMarkers,
      inviteToken ?? '',
      inviteUrl?.toString() ?? '',
      resetToken ?? '',
      resetUrl?.toString() ?? '',
    ].filter(Boolean))]
    state.privateMarkers = updatedMarkers
    state.invitationMailMessageId = recordId(inviteSummary!)
    state.resetMailMessageId = recordId(resetSummary!)
    state.resetToken = resetToken
    state.resetUrl = resetUrl?.toString()
    await writePrivateState(state)

    const newPassword = `E5-reset-${randomUUID()}aA1!`
    const resetContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
    try {
      const reset = await mutate(resetContext.request, 'POST', '/api/v1/auth/password/reset', {
        email: state.ownerEmail,
        token: resetToken,
        newPassword,
        confirmPassword: newPassword,
      })
      expect(reset.status()).toBe(204)

      const replay = await mutate(resetContext.request, 'POST', '/api/v1/auth/password/reset', {
        email: state.ownerEmail,
        token: resetToken,
        newPassword,
        confirmPassword: newPassword,
      })
      expect(replay.status()).toBe(400)
    } finally {
      await resetContext.close()
    }
  } finally {
    await mailbox.dispose()
  }

  const operatorContext = await browser.newContext({ baseURL: acceptanceBaseUrl })
  try {
    const operatorPage = await operatorContext.newPage()
    await signInThroughUi(operatorPage, state.operatorEmail, operatorPassword!)
    await operatorPage.getByRole('link', { name: 'Email delivery', exact: true }).click()
    await expect(operatorPage.getByRole('heading', { name: 'Email delivery', exact: true })).toBeVisible()

    let organizationDelivery: EmailDeliveryMetadata | undefined
    let passwordResetDelivery: EmailDeliveryMetadata | undefined
    while (Date.now() < deliveryDeadline) {
      const organizationRows = await readDeliveryPage(operatorPage.context().request, 'organization', state.organizationId, state.organizationDeliveryId)
      const identityRows = await readDeliveryPage(operatorPage.context().request, 'identity', undefined, state.identityDeliveryId)
      organizationDelivery = singleDelivery(organizationRows, (row) => row.deliveryId === state.organizationDeliveryId)
      passwordResetDelivery = singleDelivery(identityRows, (row) => row.deliveryId === state.identityDeliveryId)
      if (organizationDelivery.state === 'delivered' && passwordResetDelivery.state === 'delivered') break
      await new Promise((resolve) => setTimeout(resolve, 1000))
    }
    expect(organizationDelivery?.state).toBe('delivered')
    expect(passwordResetDelivery?.state).toBe('delivered')
    await expect(operatorPage.getByText(state.organizationDeliveryId)).toBeVisible()
    await operatorPage.getByRole('combobox', { name: 'Boundary' }).click()
    await operatorPage.getByRole('option', { name: 'Identity', exact: true }).click()
    await expect(operatorPage.getByText(state.identityDeliveryId)).toBeVisible()
    assertSafeMetadata([organizationDelivery!, passwordResetDelivery!], state.privateMarkers)

    await previewTemplate(operatorPage, 'core.organization-invitation', state.privateMarkers)
    await previewTemplate(operatorPage, 'core.password-reset', state.privateMarkers)
  } finally {
    await operatorContext.close()
  }
}

async function previewTemplate(page: Page, templateKey: string, privateMarkers: string[]) {
  const templateRow = page.getByRole('row').filter({ hasText: templateKey })
  await templateRow.getByRole('button', { name: 'Preview', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText('This is synthetic preview content, not a real message or delivery record.')).toBeVisible()
  await expect(dialog.getByText('Subject', { exact: true })).toBeVisible()
  await expect(dialog.getByText('Text body', { exact: true })).toBeVisible()
  await expect(dialog.locator('iframe')).toHaveCount(0)
  const visiblePreview = await dialog.innerText()
  for (const marker of privateMarkers) expect(visiblePreview.includes(marker)).toBe(false)
  await dialog.getByRole('button', { name: 'Close dialog' }).click()
}

async function signInThroughUi(page: Page, email: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('Email address').fill(email)
  await page.locator('input[name="password"]').fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/dashboard$/)
  await expect(page.getByRole('heading', { name: 'Platform overview' })).toBeVisible()
}

async function mutate(requestContext: APIRequestContext, method: 'POST', path: string, data: unknown) {
  const antiforgery = await getJson<{ token: string }>(requestContext, '/api/v1/auth/antiforgery')
  return requestContext.fetch(path, {
    method,
    data,
    headers: { 'X-CSRF-TOKEN': antiforgery.token },
  })
}

async function getJson<T>(requestContext: APIRequestContext, path: string): Promise<T> {
  const response = await requestContext.get(path)
  expect(response.ok(), `${path} returned ${response.status()}`).toBe(true)
  return await response.json() as T
}

async function readDeliveryPage(requestContext: APIRequestContext, boundary: 'organization' | 'identity', organizationId?: string, deliveryId?: string) {
  const params = new URLSearchParams({ boundary, page: '1', pageSize: '100' })
  if (organizationId) params.set('organizationId', organizationId)
  if (deliveryId) params.set('deliveryId', deliveryId)
  return await getJson<{ items: EmailDeliveryMetadata[] }>(requestContext, `/api/v1/platform/email-deliveries?${params.toString()}`)
}

async function waitForFixtureMessages(mailbox: APIRequestContext, recipients: string[], deadline: number) {
  let matches: UnknownRecord[] = []
  while (Date.now() < deadline) {
    const summary = await getJson<MailpitListResponse>(mailbox, '/api/v1/messages?start=0&limit=100')
    matches = summary.messages.filter((message) => messageTo(message).some((address) => recipients.includes(address)))
    if (matches.length > recipients.length) break
    if (recipients.every((recipient) => matches.filter((message) => messageTo(message).includes(recipient)).length === 1)) {
      await new Promise((resolve) => setTimeout(resolve, 1000))
      const settled = await getJson<MailpitListResponse>(mailbox, '/api/v1/messages?start=0&limit=100')
      matches = settled.messages.filter((message) => messageTo(message).some((address) => recipients.includes(address)))
      expect(settled.messages.length).toBe(recipients.length)
      if (matches.length === recipients.length) return matches
    }
    await new Promise((resolve) => setTimeout(resolve, 1000))
  }
  expect(matches.length).toBe(recipients.length)
  return matches
}

function singleDelivery(page: { items: EmailDeliveryMetadata[] }, matches: (row: EmailDeliveryMetadata) => boolean) {
  const selected = page.items.filter(matches)
  expect(selected.length).toBe(1)
  return selected[0]!
}

function assertSafeMetadata(rows: EmailDeliveryMetadata[], privateMarkers: string[]) {
  const allowed = ['deliveryId', 'boundary', 'organizationId', 'templateKey', 'templateVersion', 'state', 'attempts', 'createdAtUtc', 'nextAttemptAtUtc', 'completedAtUtc', 'failureCode', 'smtpStatusCode']
  for (const row of rows) {
    expect(Object.keys(row).every((key) => allowed.includes(key))).toBe(true)
    expect(JSON.stringify(row).includes('@')).toBe(false)
    expect(JSON.stringify(row).includes('http')).toBe(false)
    for (const marker of privateMarkers) expect(JSON.stringify(row).includes(marker)).toBe(false)
  }
}

function messageTo(message: UnknownRecord): string[] {
  const recipients = message.To ?? message.to
  if (!Array.isArray(recipients)) return []
  return recipients
    .filter((recipient): recipient is UnknownRecord => typeof recipient === 'object' && recipient !== null)
    .map((recipient) => readField(recipient, 'Address') ?? '')
    .filter(Boolean)
}

function recordId(record: UnknownRecord) {
  return readField(record, 'ID') ?? ''
}

function bodyText(message: UnknownRecord) {
  return readField(message, 'Text') ?? ''
}

function findUrl(text: string, pathFragment: string): URL | undefined {
  const candidate = text.match(/https?:\/\/[^\s<>"']+/g)?.find((value) => value.includes(pathFragment))
  if (!candidate) return undefined
  try {
    return new URL(candidate.replace(/[),.;]+$/, ''))
  } catch {
    return undefined
  }
}

function readField(record: UnknownRecord, key: string): string | undefined {
  const value = record[key] ?? record[`${key[0]!.toLowerCase()}${key.slice(1)}`]
  return typeof value === 'string' ? value : undefined
}

async function writePrivateState(state: EmailAcceptanceState) {
  const json = JSON.stringify(state)
  await writeFile(statePath!, json, { encoding: 'utf8', mode: 0o600, flag: stage === 'prepare' ? 'wx' : 'w' })
  await chmod(statePath!, 0o600)
}

async function readPrivateState(): Promise<EmailAcceptanceState> {
  const state = JSON.parse(await readFile(statePath!, 'utf8')) as EmailAcceptanceState
  expect(state.version).toBe(1)
  expect(Array.isArray(state.privateMarkers)).toBe(true)
  expect(state.privateMarkers.every((marker) => typeof marker === 'string' && marker.length > 0)).toBe(true)
  return state
}

interface EmailDeliveryMetadata {
  deliveryId: string
  boundary: string
  organizationId: string | null
  templateKey: string
  templateVersion: number | null
  state: string
  attempts: number
  createdAtUtc: string
  nextAttemptAtUtc: string | null
  completedAtUtc: string | null
  failureCode: string | null
  smtpStatusCode: number | null
}

interface EmailAcceptanceState {
  version: 1
  organizationId: string
  organizationInvitationId: string
  organizationDeliveryId: string
  identityDeliveryId: string
  operatorEmail: string
  ownerEmail: string
  inviteRecipient: string
  invitationUrl: string
  privateMarkers: string[]
  invitationMailMessageId?: string
  resetMailMessageId?: string
  resetToken?: string
  resetUrl?: string
}

interface UnknownRecord { [key: string]: unknown }
interface MailpitListResponse { messages: UnknownRecord[] }
