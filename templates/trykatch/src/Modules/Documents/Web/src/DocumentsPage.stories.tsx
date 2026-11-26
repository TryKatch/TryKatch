import type { Meta, StoryObj } from '@storybook/react-vite'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { delay, http, HttpResponse } from 'msw'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { getWorkspaceCurrentQueryKey, type DocumentDto } from '@trykatch/api-client'
import { DocumentsPage } from './index'

const organizationId = 'storybook-organization'
const permissions = ['documents.read', 'documents.manage']
const document: DocumentDto = { id: '00000000-0000-0000-0000-000000000020', title: 'Delivery invoice', documentType: 'invoice', description: 'Proof of delivery', fileName: 'invoice.pdf', createdAt: '2026-09-01T10:00:00Z', metadata: { mediaType: 'application/pdf', sizeBytes: '1024', sha256: 'demo-checksum', updatedAt: null }, lifecycle: { status: 'Active', archivedAt: null, archivedBy: null, deletedAt: null, deletedBy: null, deletionReason: null } }
const list = http.get('*/api/v1/documents/', () => HttpResponse.json([document]))
let uploadStarted = false
let uploadsCompleted = 0
let uploadOperationIds: string[] = []
let pauseWorkspaceRefresh = false
let workspaceRefreshStarted = false
let finishWorkspaceRefresh: (() => void) | undefined
const workspaceResponse = { organizationId, membershipId: 'storybook-membership', permissions }
const workspaceHandler = http.get('*/api/v1/workspace/current', async () => {
  if (pauseWorkspaceRefresh) {
    workspaceRefreshStarted = true
    await new Promise<void>((resolve) => { finishWorkspaceRefresh = resolve })
    pauseWorkspaceRefresh = false
  }
  return HttpResponse.json(workspaceResponse)
})
const meta = { title: 'Module UI/Documents', component: DocumentsPage, parameters: { msw: { handlers: {
  auth: [
    http.get('*/api/v1/access', () => HttpResponse.json({ organizationId, userId: 'storybook-user', membershipId: 'storybook-membership', permissions })),
    workspaceHandler,
    http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' })),
  ],
  api: [list],
} } } } satisfies Meta<typeof DocumentsPage>
export default meta
type Story = StoryObj<typeof meta>
export const Populated: Story = {}
export const Empty: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/documents/', () => HttpResponse.json([]))] } } } }
export const Loading: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/documents/', async () => { await delay('infinite'); return HttpResponse.json([]) })] } } } }
export const Error: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/documents/', () => HttpResponse.json({ title: 'Storage is temporarily unavailable' }, { status: 503 }))] } } } }
export const ReadOnly: Story = { parameters: { msw: { handlers: { auth: [
  http.get('*/api/v1/access', () => HttpResponse.json({ organizationId, userId: 'storybook-user', membershipId: 'storybook-membership', permissions: ['documents.read'] })),
  http.get('*/api/v1/workspace/current', () => HttpResponse.json({ organizationId, membershipId: 'storybook-membership', permissions: ['documents.read'] })),
] } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await canvas.findByText('Delivery invoice'); await expect(canvas.queryByRole('button', { name: 'Upload document' })).not.toBeInTheDocument() } }
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
function WorkspaceRefreshUploadStory() {
  const client = useQueryClient()
  useEffect(() => {
    const start = () => {
      pauseWorkspaceRefresh = true
      void client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true })
    }
    const finish = () => finishWorkspaceRefresh?.()
    window.addEventListener('storybook:start-document-workspace-refresh', start)
    window.addEventListener('storybook:finish-document-workspace-refresh', finish)
    return () => {
      window.removeEventListener('storybook:start-document-workspace-refresh', start)
      window.removeEventListener('storybook:finish-document-workspace-refresh', finish)
    }
  }, [client])
  return <DocumentsPage />
}

export const UploadDraftDuringWorkspaceRefresh: Story = {
  render: () => <WorkspaceRefreshUploadStory />,
  beforeEach: () => {
    pauseWorkspaceRefresh = false
    workspaceRefreshStarted = false
    finishWorkspaceRefresh = undefined
    uploadsCompleted = 0
  },
  parameters: { msw: { handlers: { api: [list, http.post('*/api/v1/documents/', () => {
    uploadsCompleted += 1
    return HttpResponse.json(document)
  })] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const body = within(canvasElement.ownerDocument.body)
    await canvas.findByText('Delivery invoice')
    await userEvent.click(await canvas.findByRole('button', { name: 'Upload document' }))
    const dialog = body.getByRole('dialog', { name: 'Upload document' })
    const file = new File(['story upload bytes'], 'workspace-notes.pdf', { type: 'application/pdf' })
    await userEvent.upload(within(dialog).getByLabelText('File'), file)
    await userEvent.clear(within(dialog).getByRole('textbox', { name: 'Document title' }))
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Document title' }), 'Workspace refresh draft')
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Description' }), 'Keep this draft while access refreshes')
    const staleSubmit = within(dialog).getByRole('button', { name: 'Upload document' })

    canvasElement.ownerDocument.defaultView!.dispatchEvent(new Event('storybook:start-document-workspace-refresh'))
    await waitFor(() => expect(workspaceRefreshStarted).toBe(true))
    await expect(body.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
    await expect(body.queryByText('Delivery invoice')).not.toBeInTheDocument()
    await userEvent.click(staleSubmit)
    expect(uploadsCompleted).toBe(0)

    canvasElement.ownerDocument.defaultView!.dispatchEvent(new Event('storybook:finish-document-workspace-refresh'))
    const resumed = await body.findByRole('dialog', { name: 'Upload document' })
    await expect(within(resumed).getByRole('textbox', { name: 'Document title' })).toHaveValue('Workspace refresh draft')
    await expect(within(resumed).getByRole('textbox', { name: 'Description' })).toHaveValue('Keep this draft while access refreshes')
    await expect(within(resumed).getByText(file.name)).toBeInTheDocument()
    await userEvent.click(within(resumed).getByRole('button', { name: 'Upload document' }))
    await waitFor(() => expect(uploadsCompleted).toBe(1))
  },
}
export const UploadOperationRetry: Story = {
  beforeEach: () => { uploadOperationIds = [] },
  parameters: { msw: { handlers: { api: [list, http.post('*/api/v1/documents/', async ({ request }) => {
    const form = await request.formData()
    uploadOperationIds.push(String(form.get('operationId')))
    if (uploadOperationIds.length < 3)
      return HttpResponse.json({ title: 'Storage is temporarily unavailable' }, { status: 503 })
    return HttpResponse.json(document)
  })] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('button', { name: 'Upload document' }))
    const dialog = body.getByRole('dialog', { name: 'Upload document' })
    await userEvent.upload(within(dialog).getByLabelText('File'), new File(['example'], 'invoice.pdf', { type: 'application/pdf' }))
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Document type' }), 'invoice')
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Description' }), ' Retry this upload')
    const submit = within(dialog).getByRole('button', { name: 'Upload document' })
    await userEvent.click(submit)
    await waitFor(() => expect(uploadOperationIds).toHaveLength(1))
    await waitFor(() => expect(submit).toBeEnabled())
    await userEvent.click(submit)
    await waitFor(() => expect(uploadOperationIds).toHaveLength(2))
    await expect(uploadOperationIds[1]).toBe(uploadOperationIds[0])
    await waitFor(() => expect(submit).toBeEnabled())

    const description = within(dialog).getByRole('textbox', { name: 'Description' })
    await userEvent.clear(description)
    await userEvent.type(description, ' Updated after the uncertain retry')
    await userEvent.click(submit)
    await waitFor(() => expect(uploadOperationIds).toHaveLength(3))
    await expect(uploadOperationIds[2]).not.toBe(uploadOperationIds[0])
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument())
    await expect(canvas.getByText('Delivery invoice')).toBeInTheDocument()
  },
}
export const Dark: Story = { globals: { theme: 'dark' } }
export const French: Story = { globals: { locale: 'fr' } }
export const EmptyDark: Story = { ...Empty, globals: { theme: 'dark' } }
export const EmptyFrench: Story = { ...Empty, globals: { locale: 'fr' } }
