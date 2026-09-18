import type { Meta, StoryObj } from '@storybook/react-vite'
import { delay, http, HttpResponse } from 'msw'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import type { DocumentDto } from '@trykatch/api-client'
import { DocumentsPage } from './index'

const document: DocumentDto = { id: '00000000-0000-0000-0000-000000000020', title: 'Delivery invoice', documentType: 'invoice', description: 'Proof of delivery', fileName: 'invoice.pdf', createdAt: '2026-09-01T10:00:00Z', metadata: { mediaType: 'application/pdf', sizeBytes: '1024', sha256: 'demo-checksum', updatedAt: null }, lifecycle: { status: 'Active', archivedAt: null, archivedBy: null, deletedAt: null, deletedBy: null, deletionReason: null } }
const list = http.get('*/api/v1/documents/', () => HttpResponse.json([document]))
let uploadStarted = false
const meta = { title: 'Module UI/Documents', component: DocumentsPage, parameters: { msw: { handlers: {
  auth: [http.get('*/api/v1/access', () => HttpResponse.json({ permissions: ['documents.read', 'documents.manage'] })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))],
  api: [list],
} } } } satisfies Meta<typeof DocumentsPage>
export default meta
type Story = StoryObj<typeof meta>
export const Populated: Story = {}
export const Empty: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/documents/', () => HttpResponse.json([]))] } } } }
export const Loading: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/documents/', async () => { await delay('infinite'); return HttpResponse.json([]) })] } } } }
export const Error: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/documents/', () => HttpResponse.json({ title: 'Storage is temporarily unavailable' }, { status: 503 }))] } } } }
export const ReadOnly: Story = { parameters: { msw: { handlers: { auth: [http.get('*/api/v1/access', () => HttpResponse.json({ permissions: ['documents.read'] }))] } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await canvas.findByText('Delivery invoice'); await expect(canvas.queryByRole('button', { name: 'Upload document' })).not.toBeInTheDocument() } }
export const UploadForm: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.click(await canvas.findByRole('button', { name: 'Upload document' }))
    await expect(within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Upload document' })).toBeVisible()
  },
}
export const UploadValidation: Story = {
  play: async (context) => {
    await UploadForm.play!(context)
    const dialog = within(within(context.canvasElement.ownerDocument.body).getByRole('dialog'))
    await userEvent.upload(dialog.getByLabelText('File'), new File([], 'empty.pdf', { type: 'application/pdf' }))
    await expect(await dialog.findByRole('alert')).toHaveTextContent('Choose a non-empty file')
    await userEvent.upload(dialog.getByLabelText('File'), new File(['example'], 'invoice.pdf', { type: 'application/pdf' }))
    await expect(dialog.queryByRole('alert')).not.toBeInTheDocument()
    await expect(dialog.getByRole('textbox', { name: 'Document title' })).toHaveValue('invoice')
    await userEvent.selectOptions(dialog.getByRole('combobox', { name: 'Document type' }), 'invoice')
    await expect(dialog.getByRole('button', { name: 'Upload document' })).toBeEnabled()
  },
}
export const Uploading: Story = {
  beforeEach: () => { uploadStarted = false },
  parameters: { msw: { handlers: { api: [list, http.post('*/api/v1/documents/', async () => { uploadStarted = true; await delay('infinite'); return HttpResponse.json(document) })] } } },
  play: async (context) => { await UploadValidation.play!(context); const dialog = within(within(context.canvasElement.ownerDocument.body).getByRole('dialog')); await userEvent.click(dialog.getByRole('button', { name: 'Upload document' })); await expect(await dialog.findByRole('status')).toHaveTextContent('Uploading your file'); await waitFor(() => expect(uploadStarted).toBe(true)) },
}
export const Dark: Story = { globals: { theme: 'dark' } }
export const French: Story = { globals: { locale: 'fr' } }
