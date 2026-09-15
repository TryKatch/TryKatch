// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { customFetch, type DocumentDto } from '@trykatch/api-client'
import { defineWebModule, ModuleProvider, WebModuleCatalog } from '@trykatch/module-sdk'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DocumentsPage, documentsModule } from './index'

vi.mock('@trykatch/api-client', () => ({ customFetch: vi.fn() }))

const mockedFetch = vi.mocked(customFetch)
const document: DocumentDto = {
  id: '0199ca9e-3870-7000-8000-000000000001',
  title: 'Runbook',
  description: 'Recovery steps',
  fileName: 'runbook.pdf',
  documentType: 'report',
  createdAt: '2026-09-10T12:00:00Z',
  metadata: { updatedAt: null, mediaType: 'application/pdf', sizeBytes: 4096, sha256: 'A'.repeat(64) },
  lifecycle: { status: 'Active', archivedAt: null, archivedBy: null, deletedAt: null, deletedBy: null, deletionReason: null },
}

const projectsModule = defineWebModule({
  id: 'projects', name: 'Projects', version: '1.0.0', description: 'Test host.',
  requires: [], optionalDependencies: [], routes: [], navigation: [], extensions: [],
  extensionPoints: [{ id: 'projects.list.after-table', description: 'Projects tail.', kind: 'ui-slot' }],
})
const catalog = new WebModuleCatalog([projectsModule, documentsModule])

function createClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity }, mutations: { retry: false } } })
}

function workspace(client: QueryClient, children: ReactNode) {
  return (
    <QueryClientProvider client={client}>
      <ModuleProvider catalog={catalog}>{children}</ModuleProvider>
    </QueryClientProvider>
  )
}

function renderDocuments(client = createClient()) {
  return { client, view: render(workspace(client, <DocumentsPage />)) }
}

function ShellAccessProbe() {
  const access = useQuery({
    queryKey: ['access'],
    queryFn: () => customFetch<{ permissions: string[] }>('/api/v1/access', { method: 'GET' }),
  })
  if (access.error) throw access.error
  return <p>{access.data ? `Shell permissions: ${access.data.permissions.join(',')}` : 'Shell loading'}</p>
}

describe('DocumentsPage', () => {
  beforeEach(() => mockedFetch.mockReset())
  afterEach(cleanup)

  it('opens document upload in a focused dialog instead of an inline table form', async () => {
    mockedFetch.mockImplementation(async (url) => url === '/api/v1/access'
      ? { permissions: ['documents.read', 'documents.manage'] } as never
      : [] as never)

    renderDocuments()

    const [create] = await screen.findAllByRole('button', { name: 'Upload document' })
    expect(screen.queryByRole('textbox', { name: 'Document title' })).not.toBeInTheDocument()

    fireEvent.click(create)

    expect(screen.getByRole('dialog', { name: 'Upload document' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Document title' })).toBeInTheDocument()
    expect(screen.getByLabelText('File')).toHaveAttribute('type', 'file')
  })

  it('hides every mutation control from read-only members', async () => {
    mockedFetch.mockImplementation(async (url) => url === '/api/v1/access'
      ? { permissions: ['documents.read'] }
      : [document] as never)

    renderDocuments()

    expect(await screen.findByText('Runbook')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Document title' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Runbook' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'View' }))
    expect(screen.getByRole('dialog', { name: 'Runbook' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Archive' })).not.toBeInTheDocument()
  })

  it('uploads the business type and shows the selected file without allowing dismissal or duplicate submissions while pending', async () => {
    let completeUpload: ((value: DocumentDto) => void) | undefined
    const pendingUpload = new Promise<DocumentDto>((resolve) => { completeUpload = resolve })
    mockedFetch.mockImplementation(async (url, options) => {
      if (url === '/api/v1/access') return { permissions: ['documents.read', 'documents.manage'] } as never
      if (options?.method === 'POST') return await pendingUpload as never
      return [] as never
    })
    renderDocuments()
    fireEvent.click((await screen.findAllByRole('button', { name: 'Upload document' }))[0])
    const dialog = screen.getByRole('dialog', { name: 'Upload document' })
    const submit = within(dialog).getByRole('button', { name: 'Upload document' })
    expect(submit).toBeDisabled()
    const file = new File(['invoice bytes'], 'invoice.pdf', { type: 'application/pdf' })
    fireEvent.change(within(dialog).getByLabelText('File'), { target: { files: [file] } })
    expect(within(dialog).getByText('invoice.pdf')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Document title')).toHaveValue('invoice')
    fireEvent.change(within(dialog).getByRole('combobox', { name: 'Document type' }), { target: { value: 'invoice' } })
    fireEvent.submit(dialog.querySelector('form')!)
    await screen.findByRole('status')
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled()
    expect(within(dialog).getByRole('combobox')).toBeDisabled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close dialog' }))
    expect(dialog).toBeInTheDocument()
    fireEvent.submit(dialog.querySelector('form')!)
    const writes = mockedFetch.mock.calls.filter(([, options]) => options?.method === 'POST')
    expect(writes).toHaveLength(1)
    const body = writes[0][1]?.body
    expect(body).toBeInstanceOf(FormData)
    if (!(body instanceof FormData)) throw new Error('Expected multipart upload')
    expect(body.get('documentType')).toBe('invoice')
    expect(body.get('file')).toBe(file)
    completeUpload?.({ ...document, documentType: 'invoice' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('rejects empty, oversized and unsupported files before sending any request', async () => {
    mockedFetch.mockImplementation(async (url) => url === '/api/v1/access'
      ? { permissions: ['documents.read', 'documents.manage'] } as never : [] as never)
    renderDocuments()
    fireEvent.click((await screen.findAllByRole('button', { name: 'Upload document' }))[0])
    const dialog = screen.getByRole('dialog')
    const oversized = new File(['large'], 'large.pdf', { type: 'application/pdf' })
    Object.defineProperty(oversized, 'size', { value: 25 * 1024 * 1024 + 1 })
    for (const file of [new File([], 'empty.pdf'), oversized, new File(['script'], 'run.exe')]) {
      fireEvent.change(within(dialog).getByLabelText('File'), { target: { files: [file] } })
      expect(within(dialog).getByRole('alert')).toBeInTheDocument()
      expect(within(dialog).getByRole('button', { name: 'Upload document' })).toBeDisabled()
      fireEvent.submit(dialog.querySelector('form')!)
    }
    expect(mockedFetch.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false)
    fireEvent.change(within(dialog).getByLabelText('File'), { target: { files: [new File(['ok'], 'ok.pdf')] } })
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Upload document' })).toBeEnabled()
  })

  it('shows the saved business type and updates it without uploading another file', async () => {
    mockedFetch.mockImplementation(async (url, options) => {
      if (url === '/api/v1/access') return { permissions: ['documents.read', 'documents.manage'] } as never
      if (options?.method === 'PUT') return { ...document, documentType: 'contract' } as never
      return [document] as never
    })
    renderDocuments()
    expect(await screen.findByText('Report')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Runbook' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Edit' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit document' })
    expect(within(dialog).queryByLabelText('File')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('combobox')).toHaveValue('report')
    fireEvent.change(within(dialog).getByRole('combobox'), { target: { value: 'contract' } })
    fireEvent.submit(dialog.querySelector('form')!)
    await waitFor(() => expect(mockedFetch).toHaveBeenCalledWith(`/api/v1/documents/${document.id}`,
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ title: 'Runbook', description: 'Recovery steps', documentType: 'contract' }) })))
  })

  it('distinguishes a failed read from an empty collection', async () => {
    mockedFetch.mockImplementation(async (url) => {
      if (url === '/api/v1/access') return { permissions: ['documents.read'] } as never
      if (url === '/api/v1/documents/?lifecycle=active') throw new Error('document query failed')
      return [] as never
    })

    renderDocuments()

    expect(await screen.findByText('Documents could not be loaded')).toBeInTheDocument()
    expect(screen.getByText('document query failed')).toBeInTheDocument()
    expect(screen.queryByText('No documents')).not.toBeInTheDocument()
  })

  it('surfaces save and archive failures', async () => {
    mockedFetch.mockImplementation(async (url, options) => {
      if (url === '/api/v1/access') return { permissions: ['documents.read', 'documents.manage'] } as never
      if (options?.method === 'POST' && url === '/api/v1/documents/') throw new Error('save failed')
      if (options?.method === 'POST' && String(url).endsWith('/archive')) throw new Error('archive failed')
      return [document] as never
    })

    renderDocuments()
    await screen.findByText('Runbook')
    const [openUpload] = screen.getAllByRole('button', { name: 'Upload document' })
    fireEvent.click(openUpload)
    const uploadDialog = screen.getByRole('dialog', { name: 'Upload document' })
    fireEvent.change(within(uploadDialog).getByRole('textbox', { name: 'Document title' }), { target: { value: 'New document' } })
    fireEvent.change(within(uploadDialog).getByLabelText('File'), {
      target: { files: [new File(['report'], 'report.pdf', { type: 'application/pdf' })] },
    })
    fireEvent.submit(uploadDialog.querySelector('form')!)
    await waitFor(() => expect(mockedFetch).toHaveBeenCalledWith(
      '/api/v1/documents/',
      expect.objectContaining({ method: 'POST' }),
    ))
    expect(await screen.findByText('save failed')).toHaveAttribute('role', 'alert')

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    fireEvent.click(screen.getAllByRole('button', { name: 'Upload document' })[0])
    expect(screen.queryByText('save failed')).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Document type' })).toHaveValue('other')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Runbook' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Archive' }))
    await waitFor(() => expect(screen.getByText('archive failed')).toHaveAttribute('role', 'alert'))
  })

  it('invalidates a fresh archive cache after archiving a document', async () => {
    const client = createClient()
    const archiveKey = ['archive', ['documents.read', 'documents.manage']]
    client.setQueryData(archiveKey, [])
    mockedFetch.mockImplementation(async (url, options) => {
      if (url === '/api/v1/access') return { permissions: ['documents.read', 'documents.manage'] } as never
      if (options?.method === 'POST' && String(url).endsWith('/archive')) return undefined as never
      return [document] as never
    })
    renderDocuments(client)
    await screen.findByText('Runbook')
    expect(client.getQueryState(archiveKey)?.isInvalidated).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Actions for Runbook' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Archive' }))

    await waitFor(() => expect(client.getQueryState(archiveKey)?.isInvalidated).toBe(true))
  })

  it('consumes a shell-populated access cache without changing its shape', async () => {
    const client = createClient()
    client.setQueryData(['access'], { permissions: ['documents.read', 'documents.manage'] })
    mockedFetch.mockImplementation(async (url) => url === '/api/v1/documents/?lifecycle=active' ? [] as never : { permissions: [] } as never)

    render(workspace(client, <><DocumentsPage /><ShellAccessProbe /></>))

    expect((await screen.findAllByRole('button', { name: 'Upload document' })).length).toBeGreaterThan(0)
    expect(screen.queryByRole('textbox', { name: 'Document title' })).not.toBeInTheDocument()
    expect(screen.getByText('Shell permissions: documents.read,documents.manage')).toBeInTheDocument()
    expect(client.getQueryData(['access'])).toEqual({ permissions: ['documents.read', 'documents.manage'] })
    expect(mockedFetch).not.toHaveBeenCalledWith('/api/v1/access', expect.anything())
  })

  it('leaves raw access data for the shell when Documents fetches first', async () => {
    const client = createClient()
    mockedFetch.mockImplementation(async (url) => url === '/api/v1/access'
      ? { permissions: ['documents.read', 'documents.manage'] } as never
      : [] as never)
    const { view } = renderDocuments(client)

    expect((await screen.findAllByRole('button', { name: 'Upload document' })).length).toBeGreaterThan(0)
    expect(screen.queryByRole('textbox', { name: 'Document title' })).not.toBeInTheDocument()
    expect(client.getQueryData(['access'])).toEqual({ permissions: ['documents.read', 'documents.manage'] })

    view.rerender(workspace(client, <><DocumentsPage /><ShellAccessProbe /></>))
    expect(await screen.findByText('Shell permissions: documents.read,documents.manage')).toBeInTheDocument()
  })
})
