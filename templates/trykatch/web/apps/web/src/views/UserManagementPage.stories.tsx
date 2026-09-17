import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { UserManagementPage } from './Pages'

const readers = [
  http.get('*/api/v1/members', () => HttpResponse.json([])),
  http.get('*/api/v1/invitations', () => HttpResponse.json([])),
]
const meta = {
  title: 'Application UI/Member invitations', component: UserManagementPage,
  parameters: { msw: { handlers: {
    auth: [http.get('*/api/v1/access', () => HttpResponse.json({ membershipId: 'preview', permissions: ['members.read', 'members.manage'] })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))],
    api: [...readers, http.post('*/api/v1/invitations', async ({ request }) => {
      await expect(await request.json()).toEqual({ email: 'new@example.test', expiresInDays: 7 })
      return HttpResponse.json({ invitation: { id: 'preview-invitation', email: 'new@example.test', roleId: 'default-member' }, invitationUrl: 'https://example.test/invite/preview-only', emailDelivered: false })
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

export const WithoutRoleRead: Story = {
  play: async ({ canvasElement }) => {
    await submitDefaultInvitation(canvasElement)
    await expect(await within(canvasElement.ownerDocument.body).findByRole('heading', { name: 'Invitation ready' })).toBeVisible()
  },
}
export const DefaultRoleForbidden: Story = {
  parameters: { msw: { handlers: { api: [...readers, http.post('*/api/v1/invitations', () => HttpResponse.json({ title: 'You cannot assign the default Member role.' }, { status: 403 }))] } } },
  play: async ({ canvasElement }) => {
    const dialog = await submitDefaultInvitation(canvasElement)
    await expect(await dialog.findByRole('alert')).toHaveTextContent('You cannot assign the default Member role.')
    await expect(dialog.getByRole('textbox', { name: 'Email address' })).toHaveValue('new@example.test')
  },
}
