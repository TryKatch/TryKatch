import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, type __ENTITY__Dto } from '@__NPM_SCOPE__/api-client'
import { defineWebModule, defineTableExtensionPoint, useTableContributions, type TableAction, type ArchiveLifecycle } from '@__NPM_SCOPE__/module-sdk'
import { Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Surface, type DataTableColumn } from '@__NPM_SCOPE__/ui'
import { Boxes, Plus } from 'lucide-react'
import { useState } from 'react'
import { use__MODULE__Messages } from './messages'
__WEB_DATETIME_IMPORT__

interface OrganizationAccess { permissions: string[] }
export const __MODULE_CAMEL__Table = defineTableExtensionPoint<__ENTITY__Dto>('__MODULE_ID__.list.table', '__MODULE__ list columns and row actions')
interface RecordPage { items: __ENTITY__Dto[]; page: number; pageSize: number; hasMore: boolean }
type LoadResult<T> = { value: T; failure?: never } | { value?: never; failure: string }
function isConflict(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'status' in error && error.status === 409
}

async function loadResult<T>(load: () => Promise<T>): Promise<LoadResult<T>> {
  try { return { value: await load() } }
  catch (error) { return { failure: error instanceof Error ? error.message : 'The request failed.' } }
}

export function __MODULE__Page() {
  const { t, tableLabels } = use__MODULE__Messages()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<__ENTITY__Dto | null | undefined>(undefined)
  const [viewing, setViewing] = useState<__ENTITY__Dto>()
  __WEB_FIELD_STATE__
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState('newest')
  const records = useQuery({
    queryKey: ['__MODULE_ID__s', page, search, sort],
    queryFn: () => loadResult(() => customFetch<RecordPage>(`/api/v1/__RESOURCE__/page?${new URLSearchParams({ lifecycle: 'active', page: String(page), pageSize: '25', search, sort })}`, { method: 'GET' })),
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
        body: JSON.stringify({ __WEB_REQUEST_BODY__, ...(editing ? { expectedVersion: editing.version } : {}) }),
      }),
    onSuccess: async () => {
      closeEditor()
      await queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] })
    },
  })
  const archive = useMutation({
    mutationFn: (record: __ENTITY__Dto) => customFetch<void>(`/api/v1/__RESOURCE__/${encodeURIComponent(record.id)}/archive`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedVersion: record.version }),
    }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] }),
        queryClient.invalidateQueries({ queryKey: ['archive'] }),
      ])
    },
  })
  const openCreate = () => { save.reset(); refresh.reset(); setEditing(null); __WEB_FIELD_RESET__ }
  const openEdit = (record: __ENTITY__Dto) => { save.reset(); refresh.reset(); setEditing(record); __WEB_FIELD_EDIT__ }
  const refresh = useMutation({
    mutationFn: () => {
      if (!editing) throw new Error('Select a record.')
      return customFetch<__ENTITY__Dto>(`/api/v1/__RESOURCE__/${encodeURIComponent(editing.id)}`, { method: 'GET' })
    },
    onSuccess: latest => {
      if (editing?.id === latest.id) { setEditing(latest); save.reset() }
    },
  })
  const actionsFor = (record: __ENTITY__Dto): TableAction[] => [
    { id: '__MODULE_ID__.view', label: t('view'), icon: 'view', onSelect: () => setViewing(record) },
    ...(canManage ? [
      { id: '__MODULE_ID__.edit', label: t('edit'), icon: 'edit', onSelect: () => openEdit(record) },
      { id: '__MODULE_ID__.archive', label: t('archive'), icon: 'archive', disabled: archive.isPending, onSelect: () => archive.mutate(record) },
    ] satisfies TableAction[] : []),
  ]
  const columns: DataTableColumn<__ENTITY__Dto>[] = [
    __WEB_COLUMNS__
    { id: 'actions', header: '', cell: (record) => <RowActions label={t('actionsFor', { name: __WEB_DISPLAY_VALUE__ })} actions={table.actions(record)} />, hideable: false, align: 'right', width: 54 },
  ]
  const table = useTableContributions(__MODULE_CAMEL__Table, access.data?.permissions ?? [], { columns, actions: actionsFor })
  const failure = records.data?.failure ?? access.error?.message ?? records.error?.message
  const activeRecords = records.data?.value?.items ?? []

  return <>
    <PageHeader eyebrow={t('application')} title="__MODULE__" description={t('pageDescription')}
      actions={canManage && <Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('newRecord')}</Button>} />
    <Surface className="collection">
      <form className="dialog-form" onSubmit={event => {
        event.preventDefault()
        const values = new FormData(event.currentTarget)
        setSearch(String(values.get('search') ?? '')); setSort(String(values.get('sort') ?? 'newest')); setPage(1)
      }}>
        <label>{t('searchTable')}<input name="search" type="search" maxLength={200} placeholder={t('search')} /></label>
        <label>{t('sortRecords')}<select name="sort" defaultValue="newest">
          <option value="newest">{t('newest')}</option><option value="oldest">{t('oldest')}</option>
        </select></label>
        <Button type="submit">{t('applyFilters')}</Button>
      </form>
      {archive.error && <div className="page-alert" role="alert">{archive.error.message}</div>}
      {records.isLoading || access.isLoading
        ? <p role="status">{t('loading')}</p>
        : failure
          ? <EmptyState title={t('loadFailed')} description={failure}
              action={<Button onClick={() => { records.refetch(); access.refetch() }}>{t('tryAgain')}</Button>} />
          : <DataTable labels={tableLabels} ariaLabel="__MODULE__" data={activeRecords} columns={table.columns}
              getRowId={(record) => record.id} searchable={false}
              empty={<EmptyState title={t('emptyTitle')} description={t(canManage ? 'emptyManage' : 'emptyReadOnly')}
                action={canManage ? <Button variant="primary" onClick={openCreate}>{t('createRecord')}</Button> : undefined} />} />}
      <nav aria-label={t('pageNavigation')} className="dialog-actions">
        <Button disabled={page <= 1 || records.isFetching} onClick={() => setPage(value => value - 1)}>{t('previous')}</Button>
        <span role="status">{t('currentPage', { page })}</span>
        <Button disabled={!records.data?.value?.hasMore || records.isFetching} onClick={() => setPage(value => value + 1)}>{t('next')}</Button>
      </nav>
    </Surface>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && closeEditor()}
      title={t(editing ? 'editRecord' : 'createRecord')} description={t('editorDescription')}>
      <form className="dialog-form" onSubmit={(event) => { event.preventDefault(); save.mutate() }}>
        __WEB_FORM_FIELDS__
        {save.error && <div className="form-error" role="alert">{isConflict(save.error) ? t('recordChanged') : save.error.message}</div>}
        {refresh.error && <div className="form-error" role="alert">{refresh.error.message}</div>}
        {isConflict(save.error) && <Button type="button" disabled={refresh.isPending} onClick={() => refresh.mutate()}>{t('refreshRecord')}</Button>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={closeEditor}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending || isConflict(save.error)}>{t(save.isPending ? 'saving' : 'save')}</Button>
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
  extensionPoints: [__MODULE_CAMEL__Table],
  extensions: [],
  archiveResources: [{
    kind: '__MODULE_ID__',
    typeLabel: '__ENTITY__',
    readPermission: '__MODULE_ID__.read',
    managePermission: '__MODULE_ID__.manage',
    async load() {
      const records = await customFetch<__ENTITY__Dto[]>('/api/v1/__RESOURCE__/?lifecycle=recoverable', { method: 'GET' })
      return records.map((record) => ({ id: record.id, title: __WEB_DISPLAY_VALUE__, description: __WEB_DESCRIPTION_VALUE__, lifecycle: record.lifecycle as ArchiveLifecycle, version: record.version }))
    },
    restore: (id: string, expectedVersion?: string) => customFetch<void>(`/api/v1/__RESOURCE__/${encodeURIComponent(id)}/restore`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedVersion }),
    }),
    requestDeletion: (id: string, reason: string, expectedVersion?: string) => customFetch<void>(`/api/v1/__RESOURCE__/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason, expectedVersion }),
    }),
  }],
})
