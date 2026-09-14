import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, type DocumentDto } from '@trykatch/api-client'
import { defineWebModule, ModuleExtensionSlot, useModuleI18n, type ArchiveLifecycle } from '@trykatch/module-sdk'
import { Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Surface, type DataTableColumn, type RowAction } from '@trykatch/ui'
import { Download, FileText, Plus, Upload } from 'lucide-react'
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

function formatBytes(value: number | string) {
  const bytes = Number(value)
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
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
  const [description, setDescription] = useState('')
  const [file, setFile] = useState<File>()
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
    mutationFn: async () => {
      if (editing) {
        return customFetch<DocumentDto>(`/api/v1/documents/${editing.id}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ title, description }),
        })
      }
      if (!file) throw new Error(t('Choose a file to upload.'))
      const form = new FormData()
      form.append('title', title)
      form.append('description', description)
      form.append('file', file)
      return customFetch<DocumentDto>('/api/v1/documents/', { method: 'POST', body: form })
    },
    onSuccess: async () => {
      closeEditor()
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
  const openUpload = () => {
    setEditing(null)
    setTitle('')
    setDescription('')
    setFile(undefined)
  }
  const openEdit = (document: DocumentDto) => {
    setEditing(document)
    setTitle(document.title)
    setDescription(document.description)
    setFile(undefined)
  }
  const closeEditor = () => {
    setEditing(undefined)
    setTitle('')
    setDescription('')
    setFile(undefined)
  }
  const actionsFor = (document: DocumentDto): RowAction[] => [
    { label: t('View'), icon: 'view', onSelect: () => setViewing(document) },
    ...(canManage ? [
      { label: t('Edit'), icon: 'edit', onSelect: () => openEdit(document) },
      { label: t('Archive'), icon: 'archive', disabled: archive.isPending, onSelect: () => archive.mutate(document.id) },
    ] satisfies RowAction[] : []),
  ]
  const columns: DataTableColumn<DocumentDto>[] = [
    { id: 'title', header: t('Document'), cell: (document) => <div><strong>{document.title}</strong><small>{document.fileName}</small></div>, sortValue: (document) => document.title, hideable: false },
    { id: 'description', header: t('Description'), cell: (document) => document.description || t('No description'), searchValue: (document) => `${document.fileName} ${document.description}` },
    { id: 'type', header: t('File'), cell: (document) => <div><strong>{formatBytes(document.metadata.sizeBytes)}</strong><small>{document.metadata.mediaType}</small></div>, sortValue: (document) => Number(document.metadata.sizeBytes) },
    { id: 'updated', header: t('Updated'), cell: (document) => formatDate(document.metadata.updatedAt ?? document.createdAt, { dateStyle: 'medium', timeStyle: 'short' }), sortValue: (document) => new Date(document.metadata.updatedAt ?? document.createdAt) },
    { id: 'actions', header: '', cell: (document) => <RowActions label={t('Actions for {name}', { name: document.title })} actions={actionsFor(document)} />, hideable: false, align: 'right', width: 54 },
  ]
  const loadFailure = documents.data?.failure ?? access.error?.message ?? documents.error?.message
  const documentRecords = documents.data?.value ?? []
  return <>
    <PageHeader eyebrow={t('Application')} title={t('Documents')} description={t('Upload and manage organization documents securely.')} actions={canManage && <Button variant="primary" onClick={openUpload}><Upload size={14} /> {t('Upload document')}</Button>} />
    <Surface className="collection">
      {archive.error && <div className="page-alert" role="alert">{archive.error.message}</div>}
      {access.isLoading || documents.isLoading
        ? <p role="status">{t('Loading documents…')}</p>
        : loadFailure
          ? <EmptyState title={t('Documents could not be loaded')} description={loadFailure} action={<Button onClick={() => { access.refetch(); documents.refetch() }}>{t('Try again')}</Button>} />
          : <DataTable labels={dataTableLabels} ariaLabel={t('Documents')} data={documentRecords} columns={columns} getRowId={(document) => document.id} searchPlaceholder={t('Search documents…')} initialSort={{ id: 'updated', direction: 'desc' }} empty={<EmptyState title={t('No documents')} description={t(canManage ? 'Upload the first document for this workspace.' : 'No documents are available in this workspace.')} action={canManage ? <Button variant="primary" onClick={openUpload}>{t('Upload document')}</Button> : undefined} />} />}
      <ModuleExtensionSlot point="documents.list.after-table" context={{ resultCount: documentRecords.length }} permissions={permissions} />
    </Surface>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && closeEditor()} title={t(editing ? 'Edit document' : 'Upload document')} description={t(editing ? 'Update the searchable document metadata.' : 'Choose a supported file up to 25 MB.')}>
      <form onSubmit={(event) => { event.preventDefault(); save.mutate() }} className="dialog-form">
        <label>{t('Document title')}<input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={200} required autoFocus /></label>
        <label>{t('Description')}<textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={2000} rows={4} /></label>
        {!editing && <label>{t('File')}<input type="file" accept=".pdf,.docx,.xlsx,.pptx,.txt,.csv,.jpg,.jpeg,.png,.webp" required onChange={(event) => setFile(event.target.files?.[0])} /></label>}
        {save.error && <div className="form-error" role="alert">{save.error.message}</div>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={closeEditor}>{t('Cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending}>{t(save.isPending ? (editing ? 'Saving…' : 'Uploading…') : editing ? 'Save changes' : 'Upload document')}</Button>
        </div>
      </form>
    </Dialog>
    <Dialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.title ?? t('Document')} description={t('Document details and secure download.')}>
      {viewing && <>
        <dl className="record-details document-details">
          <div><dt>{t('File')}</dt><dd>{viewing.fileName}</dd></div>
          <div><dt>{t('Size')}</dt><dd>{formatBytes(viewing.metadata.sizeBytes)}</dd></div>
          <div><dt>{t('Type')}</dt><dd>{viewing.metadata.mediaType}</dd></div>
          <div><dt>{t('Status')}</dt><dd>{t(viewing.lifecycle.status)}</dd></div>
          <div><dt>{t('Updated')}</dt><dd>{formatDate(viewing.metadata.updatedAt ?? viewing.createdAt, { dateStyle: 'medium', timeStyle: 'short' })}</dd></div>
          <div className="document-content"><dt>{t('Description')}</dt><dd>{viewing.description || t('No description')}</dd></div>
        </dl>
        <div className="dialog-actions"><Button asChild variant="primary"><a href={`/api/v1/documents/${encodeURIComponent(viewing.id)}/content`} download><Download size={14} /> {t('Download')}</a></Button></div>
      </>}
    </Dialog>
  </>
}

function ProjectDocumentsExtension() {
  const { t } = useModuleI18n()
  return <Surface className="module-extension-strip" role="region" aria-label={t('Documents workspace')}>
    <span className="module-extension-icon" aria-hidden><FileText size={17} /></span>
    <span className="module-extension-copy"><strong>{t('Documents workspace')}</strong><small>{t('Upload organization files without leaving this workspace.')}</small></span>
    <Button asChild variant="ghost"><a href="/documents">{t('Open documents')}</a></Button>
  </Surface>
}

export const documentsModule = defineWebModule({
  id: 'documents',
  name: 'Documents',
  version: '1.1.0',
  description: 'Organization-isolated object storage proof module.',
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
        description: `${document.fileName} · ${formatBytes(document.metadata.sizeBytes)}`,
        lifecycle: document.lifecycle as ArchiveLifecycle,
      }))
    },
    restore: (id: string) => customFetch<void>(`/api/v1/documents/${encodeURIComponent(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id: string, reason: string) => customFetch<void>(`/api/v1/documents/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  }],
})
