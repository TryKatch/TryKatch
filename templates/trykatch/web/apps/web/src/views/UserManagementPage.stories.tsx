import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import { UserManagementPage } from './Pages'

const readers = [
  http.get('*/api/v1/permissions', () => HttpResponse.json([])),
  http.get('*/api/v1/members', () => HttpResponse.json([])),
  http.get('*/api/v1/invitations', () => HttpResponse.json([])),
]
const meta = {
  title: 'Application UI/Member invitations', component: UserManagementPage,
  parameters: { msw: { handlers: {
    auth: [http.get('*/api/v1/access', () => HttpResponse.json({ membershipId: 'preview', permissions: ['members.read', 'members.manage'] })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))],
    api: [...readers, http.post('*/api/v1/invitations', async ({ request }) => {
      await expect(await request.json()).toEqual({ email: 'new@example.test', expiresInDays: 7 })
      // Seeded Member grants roles.read, which this caller lacks. Production
      // delegation checks therefore reject this request rather than invite.
      return HttpResponse.json({ title: 'You cannot invite a member whose access exceeds your authority.' }, { status: 403 })
    })],
  } } },
} satisfies Meta<typeof UserManagementPage>
export default meta
type Story = StoryObj<typeof meta>

async function submitDefaultInvitation(canvasElement: HTMLElement) {
  const canvas = within(canvasElement)
  await userEvent.click(await canvas.findByRole('button', { name: 'Invite person' }))
  const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
  await expect(dialog.queryByRole('radio')).not.toBeInTheDocument()
  await expect(dialog.getByText('This invitation uses the default Member role. The server checks whether you can assign it.')).toBeVisible()
  await userEvent.type(dialog.getByRole('textbox', { name: 'Email address' }), 'new@example.test')
  await expect(dialog.getByRole('button', { name: 'Create invitation' })).toBeEnabled()
  await userEvent.click(dialog.getByRole('button', { name: 'Create invitation' }))
  return dialog
}

export const DefaultRoleForbidden: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await submitDefaultInvitation(canvasElement)
    await expect(await dialog.findByRole('alert')).toHaveTextContent('You cannot invite a member whose access exceeds your authority.')
    await expect(dialog.getByRole('textbox', { name: 'Email address' })).toHaveValue('new@example.test')
  },
}

const roleReader = http.get('*/api/v1/access', () => HttpResponse.json({ membershipId: 'preview', permissions: ['members.read', 'members.manage', 'roles.read'] }))
const assignableRoles = [{ id: 'member', name: 'Member', isSystem: true, canAssign: true, lifecycle: { status: 'Active' }, permissions: [] }, { id: 'admin', name: 'Admin', isSystem: true, canAssign: true, lifecycle: { status: 'Active' }, permissions: [] }]

export const SelectWorkspaceRole: Story = {
  parameters: { msw: { handlers: { auth: [roleReader, http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))], api: [...readers, http.get('*/api/v1/roles', () => HttpResponse.json(assignableRoles)),
    http.post('*/api/v1/invitations', async ({ request }) => {
      await expect(await request.json()).toEqual({ email: 'selected@example.test', roleId: 'admin', expiresInDays: 7 })
      return HttpResponse.json({ invitation: { id: 'preview-invitation', roleId: 'admin', email: 'selected@example.test' }, emailDelivered: false, emailQueued: true, invitationUrl: 'https://example.test/accept-invitation?token=storybook-only' }, { status: 201 })
    }),
  ] } } },
  play: async ({ canvasElement }) => {
    await userEvent.click(await within(canvasElement).findByRole('button', { name: 'Invite person' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await userEvent.click(await dialog.findByRole('radio', { name: /Admin/ }))
    await expect(dialog.getByRole('radio', { name: /Admin/ })).toBeChecked()
    await expect(dialog.getByRole('textbox', { name: 'Email address' }).closest('.floating-control')).not.toBeNull()
    await userEvent.type(dialog.getByRole('textbox', { name: 'Email address' }), 'selected@example.test')
    await userEvent.click(dialog.getByRole('button', { name: 'Create invitation' }))
    await expect(await dialog.findByText('selected@example.test')).toBeVisible()
    await expect(dialog.getByText('Workspace role: Admin')).toBeVisible()
  },
}

function invitationResult(emailDelivered: boolean, emailQueued: boolean) {
  return http.post('*/api/v1/invitations', async ({ request }) => {
    await delay(100)
    await expect(await request.json()).toEqual({ email: 'new@example.test', expiresInDays: 7 })
    return HttpResponse.json({
      invitation: { id: 'preview-invitation', roleId: 'member', email: 'new@example.test' },
      invitationUrl: 'https://example.test/accept-invitation?token=storybook-only',
      emailDelivered,
      emailQueued,
    }, { status: 201 })
  })
}

async function createDefaultInvitation(canvasElement: HTMLElement, locale: 'en' | 'fr' = 'en') {
  const canvas = within(canvasElement)
  await userEvent.click(await canvas.findByRole('button', { name: locale === 'fr' ? 'Inviter une personne' : 'Invite person' }))
  const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
  await userEvent.type(dialog.getByRole('textbox', { name: locale === 'fr' ? 'Adresse e-mail' : 'Email address' }), 'new@example.test')
  await userEvent.click(dialog.getByRole('button', { name: 'Create invitation' }))
  return dialog
}

export const EmailQueued: Story = {
  globals: { theme: 'light', locale: 'en' },
  parameters: { viewport: { defaultViewport: 'phone' }, msw: { handlers: { api: [...readers, invitationResult(false, true)] } } },
  play: async ({ canvasElement }) => {
    const dialog = await createDefaultInvitation(canvasElement)
    await expect(await dialog.findByText('The invitation is queued for email delivery. Delivery has not been confirmed yet. You can also copy the link below.')).toBeVisible()
    await expect(dialog.getByText('https://example.test/accept-invitation?token=storybook-only')).toBeVisible()
    const originalClipboard = navigator.clipboard
    const copied: string[] = []
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (value: string) => { copied.push(value) } } })
    try {
      await userEvent.click(dialog.getByRole('button', { name: 'Copy invitation link' }))
      await expect(await dialog.findByRole('button', { name: 'Link copied' })).toBeVisible()
      await expect(copied).toEqual(['https://example.test/accept-invitation?token=storybook-only'])
    } finally {
      Object.defineProperty(navigator, 'clipboard', { configurable: true, value: originalClipboard })
    }
  },
}

export const EmailQueuedFrenchDark: Story = {
  globals: { theme: 'dark', locale: 'fr' },
  parameters: { viewport: { defaultViewport: 'tablet' }, msw: { handlers: { api: [...readers, invitationResult(false, true)] } } },
  play: async ({ canvasElement }) => {
    const dialog = await createDefaultInvitation(canvasElement, 'fr')
    await expect(await dialog.findByRole('heading', { name: 'Invitation prête' })).toBeVisible()
    await expect(await dialog.findByText('L’invitation est en attente d’envoi par e-mail. L’envoi n’est pas encore confirmé. Vous pouvez aussi copier le lien ci-dessous.')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Copier le lien d’invitation' })).toBeVisible()
  },
}

export const EmailDeliveryUnavailableFrench: Story = {
  globals: { theme: 'light', locale: 'fr' },
  parameters: { viewport: { defaultViewport: 'desktop' }, msw: { handlers: { api: [...readers, invitationResult(false, false)] } } },
  play: async ({ canvasElement }) => {
    const dialog = await createDefaultInvitation(canvasElement, 'fr')
    await expect(await dialog.findByRole('heading', { name: 'Invitation prête' })).toBeVisible()
    await expect(await dialog.findByText('L’envoi par e-mail est indisponible. Copiez ce lien et partagez-le avec la personne invitée par un canal sécurisé.')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Copier le lien d’invitation' })).toBeVisible()
  },
}

export const EmailDeliveredLegacyResponse: Story = {
  globals: { theme: 'dark', locale: 'en' },
  parameters: {
    viewport: { defaultViewport: 'desktop' },
    msw: { handlers: { api: [...readers, http.post('*/api/v1/invitations', () => HttpResponse.json({
      invitation: { id: 'preview-invitation', roleId: 'member', email: 'new@example.test' },
      invitationUrl: 'https://example.test/accept-invitation?token=storybook-only',
      emailDelivered: true,
    }, { status: 201 }))] } },
  },
  play: async ({ canvasElement }) => {
    const dialog = await createDefaultInvitation(canvasElement)
    await expect(await dialog.findByText('The invitation was emailed successfully. You can also copy the link below.')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Copy invitation link' })).toBeVisible()
  },
}

export const RolesLoading: Story = {
  parameters: { msw: { handlers: { auth: [roleReader], api: [...readers, http.get('*/api/v1/roles', async () => { await delay('infinite'); return HttpResponse.json([]) })] } } },
  play: async ({ canvasElement }) => {
    await userEvent.click(await within(canvasElement).findByRole('button', { name: 'Invite person' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(dialog.getByText('Loading workspace roles…')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Create invitation' })).toBeDisabled()
  },
}

export const RolesUnavailable: Story = {
  parameters: { msw: { handlers: { auth: [roleReader], api: [...readers, http.get('*/api/v1/roles', () => HttpResponse.json({ title: 'Unavailable' }, { status: 503 }))] } } },
  play: async ({ canvasElement }) => {
    await userEvent.click(await within(canvasElement).findByRole('button', { name: 'Invite person' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(await dialog.findByText('Roles could not be loaded.')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Retry loading roles' })).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Create invitation' })).toBeDisabled()
  },
}
