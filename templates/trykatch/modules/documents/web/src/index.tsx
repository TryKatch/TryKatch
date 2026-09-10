import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, type DocumentDto } from '@trykatchapp/api-client'
import { defineTrykatchWebModule, ModuleExtensionSlot, type TrykatchArchiveLifecycle } from '@trykatchapp/module-sdk'
import { Button, DataTable, EmptyState, PageHeader, Surface, type DataTableColumn } from '@trykatchapp/ui'
import { FileText, Plus } from 'lucide-react'
import { useState } from 'react'

interface OrganizationAccess { permissions: string[] }
type LoadResult<T> = { value: T; failure?: never } | { value?: never; failure: string }

async function loadResult<T>(load: () => Promise<T>): Promise<LoadResult<T>> {
  try {
    return { value: await load() }
  } catch (error) {
    return { failure: error instanceof Error ? error.message : 'The request failed.' }
  }
}

export function DocumentsPage() {
  const client = useQueryClient()
  const [editingId, setEditingId] = useState<string>()
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const documents = useQuery({
    queryKey: ['documents'],
    queryFn: () => loadResult(() => customFetch<DocumentDto[]>('/api/v1/documents/?lifecycle=active', { method: 'GET' })),
  })
  const access = useQuery({
    queryKey: ['access'],
    queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }),
  })
  const permissions = access.data?.permissions ?? []
  const canManage = permissions.includes('documents.manage')
  const save = useMutation({
    mutationFn: () => customFetch<DocumentDto>(editingId ? `/api/v1/documents/${editingId}` : '/api/v1/documents/', {
      method: editingId ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title, content }),
    }),
    onSuccess: async () => {
      setEditingId(undefined)
      setTitle('')
      setContent('')
      await client.invalidateQueries({ queryKey: ['documents'] })
    },
  })
  const archive = useMutation({
    mutationFn: (id: string) => customFetch<void>(`/api/v1/documents/${id}/archive`, { method: 'POST' }),
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: ['documents'] }),
        client.invalidateQueries({ queryKey: ['archive'] }),
      ])
    },
  })
  const edit = (document: DocumentDto) => {
    setEditingId(document.id)
    setTitle(document.title)
    setContent(document.content)
  }
  const cancelEdit = () => {
    setEditingId(undefined)
    setTitle('')
    setContent('')
  }
  const columns: DataTableColumn<DocumentDto>[] = [
    { id: 'title', header: 'Document', cell: (document) => <strong>{document.title}</strong>, sortValue: (document) => document.title, hideable: false },
    { id: 'content', header: 'Content', cell: (document) => document.content || 'Empty document', searchValue: (document) => document.content },
    { id: 'updated', header: 'Updated', cell: (document) => new Date(document.metadata.updatedAt ?? document.createdAt).toLocaleString(), sortValue: (document) => new Date(document.metadata.updatedAt ?? document.createdAt) },
    ...(canManage ? [{ id: 'actions', header: '', cell: (document: DocumentDto) => <span><Button variant="ghost" onClick={() => edit(document)}>Edit</Button><Button variant="ghost" disabled={archive.isPending} onClick={() => archive.mutate(document.id)}>Archive</Button></span>, hideable: false, align: 'right' as const }] : []),
  ]
  const loadFailure = documents.data?.failure ?? access.error?.message ?? documents.error?.message
  const documentRecords = documents.data?.value ?? []
  return <>
    <PageHeader eyebrow="Application" title="Documents" description="Organization-owned records from an independently installed module." />
    <Surface className="collection">
      {canManage && <form onSubmit={(event) => { event.preventDefault(); save.mutate() }} className="inline-form">
        <label>Document title<input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={200} required /></label>
        <label>Content<textarea value={content} onChange={(event) => setContent(event.target.value)} /></label>
        <Button type="submit" variant="primary" disabled={save.isPending}><Plus size={14} /> {editingId ? 'Save changes' : 'Create'}</Button>
        {editingId && <Button type="button" variant="ghost" onClick={cancelEdit}>Cancel</Button>}
      </form>}
      {save.error && <div className="page-alert" role="alert">{save.error.message}</div>}
      {archive.error && <div className="page-alert" role="alert">{archive.error.message}</div>}
      {access.isLoading || documents.isLoading
        ? <p role="status">Loading documents…</p>
        : loadFailure
          ? <EmptyState title="Documents could not be loaded" description={loadFailure} action={<Button onClick={() => { access.refetch(); documents.refetch() }}>Try again</Button>} />
          : <DataTable ariaLabel="Documents" data={documentRecords} columns={columns} getRowId={(document) => document.id} empty={<EmptyState title="No documents" description={canManage ? 'Create the first isolated document.' : 'No documents are available in this workspace.'} />} />}
      <ModuleExtensionSlot point="documents.list.after-table" context={{ resultCount: documentRecords.length }} permissions={permissions} />
    </Surface>
  </>
}

function ProjectDocumentsExtension() {
  return <aside aria-label="Documents module installed"><FileText size={16} /> Documents module is active.</aside>
}

export const documentsModule = defineTrykatchWebModule({
  id: 'documents',
  name: 'Documents',
  version: '1.0.0',
  description: 'Organization-owned documents proof module.',
  requires: ['projects'],
  optionalDependencies: [],
  routes: [{ id: 'documents.list', path: '/documents', component: DocumentsPage }],
  navigation: [{ id: 'documents.navigation', section: 'Workspace', order: 30, to: '/documents', label: 'Documents', icon: FileText, requiredPermission: 'documents.read' }],
  extensionPoints: [{ id: 'documents.list.after-table', description: 'Content after the documents table.', kind: 'ui-slot' }],
  extensions: [{ id: 'documents.projects-presence', point: 'projects.list.after-table', order: 30, requiredPermission: 'documents.read', component: ProjectDocumentsExtension }],
  archiveResources: [{
    kind: 'document',
    typeLabel: 'Document',
    readPermission: 'documents.read',
    managePermission: 'documents.manage',
    async load() {
      const records = await customFetch<DocumentDto[]>('/api/v1/documents/?lifecycle=recoverable', { method: 'GET' })
      return records.map((document) => ({
        id: document.id,
        title: document.title,
        description: `${document.metadata.characterCount} characters`,
        lifecycle: document.lifecycle as TrykatchArchiveLifecycle,
      }))
    },
    restore: (id: string) => customFetch<void>(`/api/v1/documents/${encodeURIComponent(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id: string, reason: string) => customFetch<void>(`/api/v1/documents/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  }],
})
