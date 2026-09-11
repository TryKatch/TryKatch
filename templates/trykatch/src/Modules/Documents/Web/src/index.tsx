import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, type DocumentDto } from '@trykatch/api-client'
import { defineWebModule, ModuleExtensionSlot, useModuleI18n, type ArchiveLifecycle } from '@trykatch/module-sdk'
import { Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Surface, type DataTableColumn, type RowAction } from '@trykatch/ui'
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
  const { t, formatDate } = useModuleI18n()
  const dataTableLabels = {
    searchTable: t('Search table'), result: t('result'), results: t('results'), columns: t('Columns'), tableSettings: t('Table settings'),
    closeTableSettings: t('Close table settings'), rowDensity: t('Row density'), compact: t('Compact'), comfortable: t('Comfortable'),
    spacious: t('Spacious'), required: t('Required'), details: t('Details'), noMatchingResults: t('No matching results.'),
    showDetails: (row: string) => t('Show details for {row}', { row }), hideDetails: (row: string) => t('Hide details for {row}', { row }),
    showing: (start: number, end: number, total: number) => t('Showing {start}–{end} of {total}', { start, end, total }),
    previous: t('Previous'), page: (page: number, count: number) => t('Page {page} of {count}', { page, count }), next: t('Next'),
  }
  const client = useQueryClient()
  const [editing, setEditing] = useState<DocumentDto | null | undefined>(undefined)
  const [viewing, setViewing] = useState<DocumentDto>()
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
    mutationFn: () => customFetch<DocumentDto>(editing ? `/api/v1/documents/${editing.id}` : '/api/v1/documents/', {
      method: editing ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title, content }),
    }),
    onSuccess: async () => {
      setEditing(undefined)
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
  const openCreate = () => {
    setEditing(null)
    setTitle('')
    setContent('')
  }
  const openEdit = (document: DocumentDto) => {
    setEditing(document)
    setTitle(document.title)
    setContent(document.content)
  }
  const closeEditor = () => {
    setEditing(undefined)
    setTitle('')
    setContent('')
  }
  const actionsFor = (document: DocumentDto): RowAction[] => [
    { label: t('View'), icon: 'view', onSelect: () => setViewing(document) },
    ...(canManage ? [
      { label: t('Edit'), icon: 'edit', onSelect: () => openEdit(document) },
      { label: t('Archive'), icon: 'archive', disabled: archive.isPending, onSelect: () => archive.mutate(document.id) },
    ] satisfies RowAction[] : []),
  ]
  const columns: DataTableColumn<DocumentDto>[] = [
    { id: 'title', header: t('Document'), cell: (document) => <div><strong>{document.title}</strong><small>{t('{count} characters', { count: document.metadata.characterCount })}</small></div>, sortValue: (document) => document.title, hideable: false },
    { id: 'content', header: t('Content'), cell: (document) => document.content || t('Empty document'), searchValue: (document) => document.content },
    { id: 'updated', header: t('Updated'), cell: (document) => formatDate(document.metadata.updatedAt ?? document.createdAt, { dateStyle: 'medium', timeStyle: 'short' }), sortValue: (document) => new Date(document.metadata.updatedAt ?? document.createdAt) },
    { id: 'actions', header: '', cell: (document) => <RowActions label={t('Actions for {name}', { name: document.title })} actions={actionsFor(document)} />, hideable: false, align: 'right', width: 54 },
  ]
  const loadFailure = documents.data?.failure ?? access.error?.message ?? documents.error?.message
  const documentRecords = documents.data?.value ?? []
  return <>
    <PageHeader eyebrow={t('Application')} title={t('Documents')} description={t('Create and manage organization documents.')} actions={canManage && <Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('New document')}</Button>} />
    <Surface className="collection">
      {archive.error && <div className="page-alert" role="alert">{archive.error.message}</div>}
      {access.isLoading || documents.isLoading
        ? <p role="status">{t('Loading documents…')}</p>
        : loadFailure
          ? <EmptyState title={t('Documents could not be loaded')} description={loadFailure} action={<Button onClick={() => { access.refetch(); documents.refetch() }}>{t('Try again')}</Button>} />
          : <DataTable labels={dataTableLabels} ariaLabel={t('Documents')} data={documentRecords} columns={columns} getRowId={(document) => document.id} searchPlaceholder={t('Search documents…')} initialSort={{ id: 'updated', direction: 'desc' }} empty={<EmptyState title={t('No documents')} description={t(canManage ? 'Create the first document for this workspace.' : 'No documents are available in this workspace.')} action={canManage ? <Button variant="primary" onClick={openCreate}>{t('Create document')}</Button> : undefined} />} />}
      <ModuleExtensionSlot point="documents.list.after-table" context={{ resultCount: documentRecords.length }} permissions={permissions} />
    </Surface>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && closeEditor()} title={t(editing ? 'Edit document' : 'Create document')} description={t('Keep the title clear and the content focused.')}>
      <form onSubmit={(event) => { event.preventDefault(); save.mutate() }} className="dialog-form">
        <label>{t('Document title')}<input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={200} required autoFocus /></label>
        <label>{t('Content')}<textarea value={content} onChange={(event) => setContent(event.target.value)} rows={8} /></label>
        {save.error && <div className="form-error" role="alert">{save.error.message}</div>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={closeEditor}>{t('Cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending}>{t(save.isPending ? 'Saving…' : editing ? 'Save changes' : 'Create document')}</Button>
        </div>
      </form>
    </Dialog>
    <Dialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.title ?? t('Document')} description={t('Document details and content.')}>
      {viewing && <dl className="record-details document-details">
        <div><dt>{t('Status')}</dt><dd>{t(viewing.lifecycle.status)}</dd></div>
        <div><dt>{t('Updated')}</dt><dd>{formatDate(viewing.metadata.updatedAt ?? viewing.createdAt, { dateStyle: 'medium', timeStyle: 'short' })}</dd></div>
        <div className="document-content"><dt>{t('Content')}</dt><dd>{viewing.content || t('Empty document')}</dd></div>
      </dl>}
    </Dialog>
  </>
}

function ProjectDocumentsExtension() {
  const { t } = useModuleI18n()
  return <Surface className="module-extension-strip" role="region" aria-label={t('Documents workspace')}>
    <span className="module-extension-icon" aria-hidden><FileText size={17} /></span>
    <span className="module-extension-copy"><strong>{t('Documents workspace')}</strong><small>{t('Manage organization documents without leaving this workspace.')}</small></span>
    <Button asChild variant="ghost"><a href="/documents">{t('Open documents')}</a></Button>
  </Surface>
}

export const documentsModule = defineWebModule({
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
        lifecycle: document.lifecycle as ArchiveLifecycle,
      }))
    },
    restore: (id: string) => customFetch<void>(`/api/v1/documents/${encodeURIComponent(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id: string, reason: string) => customFetch<void>(`/api/v1/documents/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  }],
})
