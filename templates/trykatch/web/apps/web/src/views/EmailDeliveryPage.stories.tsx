import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import { EmailDeliveryPage } from './EmailDeliveryPage'

const delivery = {
  deliveryId: 'delivery-1', boundary: 'organization', organizationId: 'org-1', templateKey: 'organization.invitation', templateVersion: 1,
  state: 'queued', attempts: 0, createdAtUtc: '2026-10-24T09:30:00Z', nextAttemptAtUtc: null, completedAtUtc: null, failureCode: null, smtpStatusCode: null,
}
const template = { templateKey: 'organization.invitation', templateVersion: 1 }
const auth = (permissions: string[], isPlatformAdministrator = false) => http.get('*/api/v1/auth/session', () => HttpResponse.json({ displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, isPlatformAdministrator, platformPermissions: permissions }))
const deliveries = (items = [delivery], totalCount = items.length) => http.get('*/api/v1/platform/email-deliveries', () => HttpResponse.json({ items, page: 1, pageSize: 50, totalCount }))
const templates = (items = [template], totalCount = items.length) => http.get('*/api/v1/platform/email-deliveries/templates', () => HttpResponse.json({ items, page: 1, pageSize: 50, totalCount }))
const preview = http.get('*/api/v1/platform/email-deliveries/templates/preview', () => HttpResponse.json({
  template,
  subject: 'Invitation for Ada',
  textBody: 'Synthetic invitation preview for Ada.',
  htmlBody: '<script>throw new Error("must not render")</script><p>Preview only</p>',
}))

const meta = {
  title: 'Application UI/Email delivery',
  component: EmailDeliveryPage,
  parameters: { msw: { handlers: { auth: [auth(['platform.email.read'])], api: [deliveries(), templates(), preview] } } },
} satisfies Meta<typeof EmailDeliveryPage>
export default meta
type Story = StoryObj<typeof meta>

export const DeliveryRecords: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('delivery-1')).toBeVisible()
    const table = within(canvas.getByRole('table', { name: 'Email delivery records' }))
    await expect(table.getByText('organization.invitation')).toBeVisible()
    await expect(table.getByText('queued')).toBeVisible()
    await expect(table.getByText('org-1')).toBeVisible()
  },
}

export const BoundaryStateAndPagination: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/email-deliveries', async ({ request }) => {
    await delay(100)
    const params = new URL(request.url).searchParams
    expect(params.get('pageSize')).toBe('50')
    if (params.get('boundary') === 'identity' && params.get('state') === 'terminal') {
      expect(params.get('page')).toBe('1')
      expect(params.get('organizationId')).toBeNull()
      expect(params.get('deliveryId')).toBe('delivery-2')
      return HttpResponse.json({ items: [{ ...delivery, boundary: 'identity', organizationId: null, deliveryId: 'delivery-2', state: 'terminal', attempts: '5' }], page: 1, pageSize: 50, totalCount: 1 })
    }
    if (params.get('state') === 'terminal') {
      expect(params.get('boundary')).toBe('organization')
      expect(params.get('organizationId')).toBe('org-2')
      expect(params.get('deliveryId')).toBe('delivery-2')
      return HttpResponse.json({ items: [{ ...delivery, deliveryId: 'delivery-2', state: 'terminal', attempts: '5' }], page: 1, pageSize: 50, totalCount: 1 })
    }
    return HttpResponse.json({ items: [delivery], page: Number(params.get('page') ?? 1), pageSize: 50, totalCount: 51 })
  }), templates()] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('delivery-1')
    await userEvent.type(canvas.getByRole('textbox', { name: 'Organization ID' }), 'org-2')
    await userEvent.type(canvas.getByRole('textbox', { name: 'Delivery ID' }), 'delivery-2')
    await userEvent.click(canvas.getByRole('button', { name: 'Apply filters' }))
    const deliverySection = within(canvas.getByRole('region', { name: 'Delivery records' }))
    const nextDeliveriesPage = await deliverySection.findByRole('button', { name: 'Next' })
    await expect(nextDeliveriesPage).toBeEnabled()
    await userEvent.click(nextDeliveriesPage)
    await expect(await canvas.findByText('Page 2 of 2')).toBeVisible()
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Filter by status' }))
    await userEvent.click(within(canvasElement.ownerDocument.body).getByRole('option', { name: 'terminal' }))
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Boundary' }))
    await userEvent.click(within(canvasElement.ownerDocument.body).getByRole('option', { name: 'Identity' }))
    await expect(canvas.queryByRole('textbox', { name: 'Organization ID' })).not.toBeInTheDocument()
    const table = within(await canvas.findByRole('table', { name: 'Email delivery records' }))
    await expect(await table.findByText('delivery-2')).toBeVisible()
    await expect(table.getByText('Identity')).toBeVisible()
    await expect(table.getByText('terminal')).toBeVisible()
    await expect(table.getByText('5')).toBeVisible()
  },
}

export const AdministratorNeedsExplicitPermission: Story = {
  globals: { theme: 'dark', locale: 'fr' },
  parameters: { msw: { handlers: { auth: [auth([], true)], api: [
    http.get('*/api/v1/platform/email-deliveries', () => { throw new Error('Denied session must not fetch delivery records') }),
    http.get('*/api/v1/platform/email-deliveries/templates', () => { throw new Error('Denied session must not fetch templates') }),
  ] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByRole('alert')).toHaveTextContent('Vous n’avez pas l’autorisation de consulter les envois d’e-mails.')
    await expect(canvas.queryByText('delivery-1')).not.toBeInTheDocument()
    await expect(canvas.queryByRole('combobox', { name: 'Boundary' })).not.toBeInTheDocument()
  },
}

export const Loading: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/email-deliveries', async () => { await delay('infinite'); return HttpResponse.json({}) }), templates()] } } },
  play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByRole('status', { name: 'Loading email delivery records' })).toBeVisible() },
}

export const Empty: Story = {
  parameters: { msw: { handlers: { api: [deliveries([]), templates([])] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('No email delivery records')).toBeVisible()
    await expect(await canvas.findByText('No email templates')).toBeVisible()
  },
}

export const LoadFailure: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/email-deliveries', () => new HttpResponse(null, { status: 503 })), templates()] } } },
  play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByRole('alert')).toHaveTextContent('Email delivery records could not be loaded.') },
}

export const TemplatePreview: Story = {
  globals: { theme: 'dark', locale: 'fr' },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const templateTable = within(await canvas.findByRole('table', { name: 'Modèles d’e-mail' }))
    await templateTable.findByText('organization.invitation')
    await userEvent.click(templateTable.getByRole('button', { name: 'Prévisualiser' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(await dialog.findByText('Ce contenu de prévisualisation est fictif ; il ne s’agit pas d’un message ni d’un envoi réel.')).toBeVisible()
    await expect(dialog.getByText('Invitation for Ada')).toBeVisible()
    await expect(dialog.getByText('Synthetic invitation preview for Ada.')).toBeVisible()
    await expect(dialog.queryByRole('img')).not.toBeInTheDocument()
    await expect(dialog.queryByText(/must not render/)).not.toBeInTheDocument()
  },
}

export const TemplatePreviewFailure: Story = {
  parameters: { msw: { handlers: { api: [deliveries(), templates(), http.get('*/api/v1/platform/email-deliveries/templates/preview', () => new HttpResponse(null, { status: 503 }))] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const templateTable = within(await canvas.findByRole('table', { name: 'Email templates' }))
    await templateTable.findByText('organization.invitation')
    await userEvent.click(templateTable.getByRole('button', { name: 'Preview' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(await dialog.findByRole('alert')).toHaveTextContent('Template preview could not be loaded.')
    await expect(dialog.getByRole('button', { name: 'Retry loading' })).toBeVisible()
  },
}

export const French: Story = {
  globals: { theme: 'light', locale: 'fr' },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByRole('heading', { name: 'Envoi des e-mails' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Enregistrements d’envoi' })).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Périmètre' })).toBeVisible()
    const records = within(await canvas.findByRole('table', { name: 'Enregistrements d’envoi' }))
    await expect(canvas.getAllByRole('searchbox', { name: 'Rechercher dans le tableau' })).toHaveLength(2)
    await expect(canvas.getAllByText('Colonnes')).toHaveLength(2)
    await expect(records.getByText('Organisation')).toBeVisible()
    const templateTable = within(await canvas.findByRole('table', { name: 'Modèles d’e-mail' }))
    await expect(templateTable.getByRole('columnheader', { name: 'Modèle' })).toBeVisible()
  },
}
