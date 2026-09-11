// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { customFetch, type DocumentDto } from '@trykatch/api-client'
import { defineWebModule, ModuleProvider, WebModuleCatalog } from '@trykatch/module-sdk'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DocumentsPage, documentsModule } from './index'

vi.mock('@trykatch/api-client', () => ({ customFetch: vi.fn() }))

const mockedFetch = vi.mocked(customFetch)
const document: DocumentDto = {
  id: '0199ca9e-3870-7000-8000-000000000001',
  title: 'Runbook',
  content: 'Recovery steps',
  createdAt: '2026-09-10T12:00:00Z',
  metadata: { updatedAt: null, mediaType: 'text/plain', characterCount: 14 },
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

  it('opens document creation in a focused dialog instead of an inline table form', async () => {
    mockedFetch.mockImplementation(async (url) => url === '/api/v1/access'
      ? { permissions: ['documents.read', 'documents.manage'] } as never
      : [] as never)

    renderDocuments()

    const create = await screen.findByRole('button', { name: 'New document' })
    expect(screen.queryByRole('textbox', { name: 'Document title' })).not.toBeInTheDocument()

    fireEvent.click(create)

    expect(screen.getByRole('dialog', { name: 'Create document' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Document title' })).toBeInTheDocument()
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
    fireEvent.click(screen.getByRole('button', { name: 'New document' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Document title' }), { target: { value: 'New document' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create document' }))
    expect(await screen.findByText('save failed')).toHaveAttribute('role', 'alert')

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

    expect(await screen.findByRole('button', { name: 'New document' })).toBeInTheDocument()
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

    expect(await screen.findByRole('button', { name: 'New document' })).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Document title' })).not.toBeInTheDocument()
    expect(client.getQueryData(['access'])).toEqual({ permissions: ['documents.read', 'documents.manage'] })

    view.rerender(workspace(client, <><DocumentsPage /><ShellAccessProbe /></>))
    expect(await screen.findByText('Shell permissions: documents.read,documents.manage')).toBeInTheDocument()
  })
})
