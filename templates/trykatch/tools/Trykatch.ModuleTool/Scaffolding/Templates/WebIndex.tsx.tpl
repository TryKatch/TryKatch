import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, type __ENTITY__Dto } from '@__NPM_SCOPE__/api-client'
import { defineWebModule, type ArchiveLifecycle } from '@__NPM_SCOPE__/module-sdk'
import { Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Surface, type DataTableColumn, type RowAction } from '@__NPM_SCOPE__/ui'
import { Boxes, Plus } from 'lucide-react'
import { useState } from 'react'
import { use__MODULE__Messages } from './messages'

interface OrganizationAccess { permissions: string[] }
type LoadResult<T> = { value: T; failure?: never } | { value?: never; failure: string }

async function loadResult<T>(load: () => Promise<T>): Promise<LoadResult<T>> {
  try { return { value: await load() } }
  catch (error) { return { failure: error instanceof Error ? error.message : 'The request failed.' } }
}

__WEB_HELPERS__

export function __MODULE__Page() {
  const { t, tableLabels } = use__MODULE__Messages()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<__ENTITY__Dto | null | undefined>(undefined)
  const [viewing, setViewing] = useState<__ENTITY__Dto>()
  __WEB_FIELD_STATE__
  const records = useQuery({
    queryKey: ['__MODULE_ID__s'],
    queryFn: () => loadResult(() => customFetch<__ENTITY__Dto[]>('/api/v1/__RESOURCE__/?lifecycle=active', { method: 'GET' })),
  })
  const access = useQuery({
    queryKey: ['access'],
    queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }),
  })
  const canManage = (access.data?.permissions ?? []).includes('__MODULE_ID__.manage')
  const closeEditor = () => { setEditing(undefined); __WEB_FIELD_RESET__ }
  const save = useMutation({
    mutationFn: () => customFetch<__ENTITY__Dto>(editing
      ? `/api/v1/__RESOURCE__/${encodeURIComponent(editing.id)}`
      : '/api/v1/__RESOURCE__/', {
        method: editing ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ __WEB_REQUEST_BODY__ }),
      }),
    onSuccess: async () => {
      closeEditor()
      await queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] })
    },
  })
  const archive = useMutation({
    mutationFn: (id: string) => customFetch<void>(`/api/v1/__RESOURCE__/${encodeURIComponent(id)}/archive`, { method: 'POST' }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] }),
        queryClient.invalidateQueries({ queryKey: ['archive'] }),
      ])
    },
  })
  const openCreate = () => { setEditing(null); __WEB_FIELD_RESET__ }
  const openEdit = (record: __ENTITY__Dto) => { setEditing(record); __WEB_FIELD_EDIT__ }
  const actionsFor = (record: __ENTITY__Dto): RowAction[] => [
    { label: t('view'), icon: 'view', onSelect: () => setViewing(record) },
    ...(canManage ? [
      { label: t('edit'), icon: 'edit', onSelect: () => openEdit(record) },
      { label: t('archive'), icon: 'archive', disabled: archive.isPending, onSelect: () => archive.mutate(record.id) },
    ] satisfies RowAction[] : []),
  ]
  const columns: DataTableColumn<__ENTITY__Dto>[] = [
    __WEB_COLUMNS__
    { id: 'actions', header: '', cell: (record) => <RowActions label={t('actionsFor', { name: __WEB_DISPLAY_VALUE__ })} actions={actionsFor(record)} />, hideable: false, align: 'right', width: 54 },
  ]
  const failure = records.data?.failure ?? access.error?.message ?? records.error?.message
  const activeRecords = records.data?.value ?? []

  return <>
    <PageHeader eyebrow={t('application')} title="__MODULE__" description={t('pageDescription')}
      actions={canManage && <Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('newRecord')}</Button>} />
    <Surface className="collection">
      {archive.error && <div className="page-alert" role="alert">{archive.error.message}</div>}
      {records.isLoading || access.isLoading
        ? <p role="status">{t('loading')}</p>
        : failure
          ? <EmptyState title={t('loadFailed')} description={failure}
              action={<Button onClick={() => { records.refetch(); access.refetch() }}>{t('tryAgain')}</Button>} />
          : <DataTable labels={tableLabels} ariaLabel="__MODULE__" data={activeRecords} columns={columns}
              getRowId={(record) => record.id} searchPlaceholder={t('search')}
              empty={<EmptyState title={t('emptyTitle')} description={t(canManage ? 'emptyManage' : 'emptyReadOnly')}
                action={canManage ? <Button variant="primary" onClick={openCreate}>{t('createRecord')}</Button> : undefined} />} />}
    </Surface>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && closeEditor()}
      title={t(editing ? 'editRecord' : 'createRecord')} description={t('editorDescription')}>
      <form className="dialog-form" onSubmit={(event) => { event.preventDefault(); save.mutate() }}>
        __WEB_FORM_FIELDS__
        {save.error && <div className="form-error" role="alert">{save.error.message}</div>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={closeEditor}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending}>{t(save.isPending ? 'saving' : 'save')}</Button>
        </div>
      </form>
    </Dialog>
    <Dialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)}
      title={viewing ? __WEB_VIEWING_DISPLAY_VALUE__ : '__ENTITY__'} description={t('recordDetails')}>
      {viewing && <dl className="record-details">
        <div><dt>{t('status')}</dt><dd>{viewing.lifecycle.status}</dd></div>
        __WEB_DETAIL_FIELDS__
      </dl>}
    </Dialog>
  </>
}

export const __MODULE_CAMEL__Module = defineWebModule({
  id: '__MODULE_ID__',
  name: '__MODULE__',
  version: '1.0.0',
  description: '__DESCRIPTION__',
  requires: [],
  optionalDependencies: [],
  routes: [{ id: '__MODULE_ID__.list', path: '/__RESOURCE__', component: __MODULE__Page }],
  navigation: [{ id: '__MODULE_ID__.navigation', section: 'Workspace', order: 50, to: '/__RESOURCE__', label: '__MODULE__', icon: Boxes, requiredPermission: '__MODULE_ID__.read' }],
  extensionPoints: [],
  extensions: [],
  archiveResources: [{
    kind: '__MODULE_ID__',
    typeLabel: '__ENTITY__',
    readPermission: '__MODULE_ID__.read',
    managePermission: '__MODULE_ID__.manage',
    async load() {
      const records = await customFetch<__ENTITY__Dto[]>('/api/v1/__RESOURCE__/?lifecycle=recoverable', { method: 'GET' })
      return records.map((record) => ({ id: record.id, title: __WEB_DISPLAY_VALUE__, description: __WEB_DESCRIPTION_VALUE__, lifecycle: record.lifecycle as ArchiveLifecycle }))
    },
    restore: (id: string) => customFetch<void>(`/api/v1/__RESOURCE__/${encodeURIComponent(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id: string, reason: string) => customFetch<void>(`/api/v1/__RESOURCE__/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  }],
})
