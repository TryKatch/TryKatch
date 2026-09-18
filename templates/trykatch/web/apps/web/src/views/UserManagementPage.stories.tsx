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
  parameters: { msw: { handlers: { auth: [roleReader], api: [...readers, http.get('*/api/v1/roles', () => HttpResponse.json(assignableRoles))] } } },
  play: async ({ canvasElement }) => {
    await userEvent.click(await within(canvasElement).findByRole('button', { name: 'Invite person' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await userEvent.click(await dialog.findByRole('radio', { name: /Admin/ }))
    await expect(dialog.getByRole('radio', { name: /Admin/ })).toBeChecked()
    await expect(dialog.getByRole('textbox', { name: 'Email address' }).closest('.floating-control')).not.toBeNull()
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
