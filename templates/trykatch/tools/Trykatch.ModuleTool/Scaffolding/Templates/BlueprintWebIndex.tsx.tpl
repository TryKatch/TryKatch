import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, __MODULE_CAMEL__List, __MODULE_CAMEL__Create, __MODULE_CAMEL__Update, __MODULE_CAMEL__Archive, __MODULE_CAMEL__Restore, __MODULE_CAMEL__RequestDeletion, type __ENTITY__Dto } from '@__NPM_SCOPE__/api-client'
import { defineWebModule, defineTableExtensionPoint, useTableContributions, type TableAction, type ArchiveLifecycle } from '@__NPM_SCOPE__/module-sdk'
import { Button, DataTable, Dialog, EmptyState, FloatingInput, FloatingTextarea, PageHeader, RowActions, Surface, type DataTableColumn } from '@__NPM_SCOPE__/ui'
import { Boxes, Plus } from 'lucide-react'
import { useRef, useState } from 'react'
import { workflowActions, runWorkflowAction, workflowText, workflowError, isStaleConflict } from './workflow'
import { use__MODULE__Messages } from './messages'
__WEB_DATETIME_IMPORT__

interface OrganizationAccess { permissions: string[] }
export const __MODULE_CAMEL__Table = defineTableExtensionPoint<__ENTITY__Dto>('__MODULE_ID__.list.table', '__MODULE__ list columns and row actions')
interface RecordPage { items: __ENTITY__Dto[]; page: number; pageSize: number; hasMore: boolean }
type LoadResult<T> = { value: T; failure?: never } | { value?: never; failure: string }

async function loadResult<T>(load: () => Promise<T>): Promise<LoadResult<T>> {
  try { return { value: await load() } }
  catch (error) { return { failure: error instanceof Error ? error.message : 'The request failed.' } }
}

export function __MODULE__Page() {
  const { t, tableLabels, locale } = use__MODULE__Messages()
  const queryClient = useQueryClient()
  const refreshScope = useRef(0)
  const [actionRecord, setActionRecord] = useState<__ENTITY__Dto>()
  const [selectedAction, setSelectedAction] = useState<string>()
  const [actionValues, setActionValues] = useState<Record<string, string>>({})
  const actionDefinition = workflowActions.find(action => action.id === selectedAction)
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
  const closeEditor = () => { refreshScope.current++; refresh.reset(); setEditing(undefined); __WEB_FIELD_RESET__ }
  const save = useMutation({
    mutationFn: () => editing
      ? __MODULE_CAMEL__Update(editing.id, { __WEB_REQUEST_BODY__, expectedVersion: editing.version })
      : __MODULE_CAMEL__Create({ __WEB_REQUEST_BODY__ }),
    onSuccess: async () => {
      closeEditor()
      await queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] })
    },
  })
  const archive = useMutation({
    mutationFn: (record: __ENTITY__Dto) => __MODULE_CAMEL__Archive(record.id, { expectedVersion: record.version }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] }),
        queryClient.invalidateQueries({ queryKey: ['archive'] }),
      ])
    },
  })
  const transition = useMutation({
    mutationFn: () => {
      if (!selectedAction || !actionRecord) throw new Error('Select an action.')
      return runWorkflowAction(selectedAction, actionRecord.id, actionRecord.version, actionValues)
    },
    onSuccess: async () => {
      setSelectedAction(undefined)
      setActionRecord(undefined)
      setViewing(undefined)
      setActionValues({})
      await queryClient.invalidateQueries({ queryKey: ['__MODULE_ID__s'] })
    },
  })
  const closeAction = () => { refreshScope.current++; refresh.reset(); setSelectedAction(undefined); setActionRecord(undefined); setActionValues({}); transition.reset() }
  const openAction = (record: __ENTITY__Dto, id: string) => { refreshScope.current++; transition.reset(); refresh.reset(); setActionRecord(record); setSelectedAction(id); setActionValues({}) }
  const openCreate = () => { refreshScope.current++; save.reset(); refresh.reset(); setEditing(null); __WEB_FIELD_RESET__ }
  const openEdit = (record: __ENTITY__Dto) => { refreshScope.current++; save.reset(); refresh.reset(); setEditing(record); __WEB_FIELD_EDIT__ }
  const refresh = useMutation({
    mutationFn: async (id: string) => {
      const scope = refreshScope.current
      const latest = await customFetch<__ENTITY__Dto>(`/api/v1/__RESOURCE__/${encodeURIComponent(id)}`, { method: 'GET' })
      return { latest, scope }
    },
    onSuccess: ({ latest, scope }) => {
      if (scope !== refreshScope.current) return
      if (editing?.id === latest.id) { setEditing(latest); save.reset() }
      if (actionRecord?.id === latest.id) { setActionRecord(latest); transition.reset() }
    },
  })
  const actionsFor = (record: __ENTITY__Dto): TableAction[] => [
    { id: '__MODULE_ID__.view', label: t('view'), icon: 'view', onSelect: () => setViewing(record) },
    ...(canManage ? [
      { id: '__MODULE_ID__.edit', label: t('edit'), icon: 'edit', disabled: !record.canEdit, onSelect: () => openEdit(record) },
      { id: '__MODULE_ID__.archive', label: t('archive'), icon: 'archive', disabled: archive.isPending, onSelect: () => archive.mutate(record) },
    ] satisfies TableAction[] : []),
    ...workflowActions.filter(action => record.availableActions.includes(action.id)).map((action): TableAction => ({ id: '__MODULE_ID__.' + action.id, label: workflowText(action.label, locale), icon: 'edit', disabled: transition.isPending, onSelect: () => openAction(record, action.id) })),
  ]
  const columns: DataTableColumn<__ENTITY__Dto>[] = [
    { id: 'workflowState', header: t('workflowState'), cell: record => workflowText(record.workflowState, locale) },
    __WEB_COLUMNS__
    { id: 'actions', header: '', cell: (record) => <RowActions label={t('actionsFor', { name: __WEB_DISPLAY_VALUE__ })} actions={table.actions(record)} />, hideable: false, align: 'right', width: 54 },
  ]
  const table = useTableContributions(__MODULE_CAMEL__Table, access.data?.permissions ?? [], { columns, actions: actionsFor })
  const failure = records.data?.failure ?? access.error?.message ?? records.error?.message
  const activeRecords = records.data?.value?.items ?? []

  return <>
    <PageHeader eyebrow={t('application')} title={t('moduleTitle')} description={t('pageDescription')}
      actions={canManage && <Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('newRecord')}</Button>} />
    <Surface className="collection">
      <form className="dialog-form" onSubmit={event => {
        event.preventDefault()
        const values = new FormData(event.currentTarget)
        setSearch(String(values.get('search') ?? '')); setSort(String(values.get('sort') ?? 'newest')); setPage(1)
      }}>
        <FloatingInput label={t('searchTable')} name="search" type="search" maxLength={200} />
        <label>{t('sortRecords')}<select name="sort" defaultValue="newest">
          <option value="newest">{t('newest')}</option><option value="oldest">{t('oldest')}</option>
        </select></label>
        <Button type="submit">{t('applyFilters')}</Button>
      </form>
      {archive.error && <div className="page-alert" role="alert">{workflowError(archive.error, locale)}</div>}
      {records.isLoading || access.isLoading
        ? <p role="status">{t('loading')}</p>
        : failure
          ? <EmptyState title={t('loadFailed')} description={failure}
              action={<Button onClick={() => { records.refetch(); access.refetch() }}>{t('tryAgain')}</Button>} />
          : <DataTable labels={tableLabels} ariaLabel={t('moduleTitle')} data={activeRecords} columns={table.columns}
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
        {save.error && <div className="form-error" role="alert">{workflowError(save.error, locale)}</div>}
        {editing && <p role="status">{t('workflowState')}: {workflowText(editing.workflowState, locale)}</p>}
        {refresh.error && <div className="form-error" role="alert">{workflowError(refresh.error, locale)}</div>}
        {isStaleConflict(save.error) && editing && <Button type="button" disabled={refresh.isPending} onClick={() => refresh.mutate(editing.id)}>{t('refreshRecord')}</Button>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={closeEditor}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending || isStaleConflict(save.error) || (editing != null && !editing.canEdit)}>{t(save.isPending ? 'saving' : 'save')}</Button>
        </div>
      </form>
    </Dialog>
    <Dialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)}
      title={viewing ? __WEB_VIEWING_DISPLAY_VALUE__ : '__ENTITY__'} description={t('recordDetails')}>
      {viewing && <dl className="record-details">
        <div><dt>{t('status')}</dt><dd>{viewing.lifecycle.status}</dd></div>
        <div><dt>{t('workflowState')}</dt><dd>{workflowText(viewing.workflowState, locale)}</dd></div>
        {viewing.decisionReason && <div><dt>{t('decisionReason')}</dt><dd>{viewing.decisionReason}</dd></div>}
        __WEB_DETAIL_FIELDS__
      </dl>}
    </Dialog>
    <Dialog open={selectedAction !== undefined} onOpenChange={open => !open && closeAction()}
      title={actionDefinition ? workflowText(actionDefinition.label, locale) : t('actionsFor', { name: '' })}
      description={t('actionDescription')}>
      <form className="dialog-form" onSubmit={event => { event.preventDefault(); transition.mutate() }}>
        {actionDefinition?.inputs.map(input => <FloatingTextarea key={input.name} label={workflowText(input.label, locale)}
          name={input.name} required={input.required} minLength={input.minimumLength ?? undefined} maxLength={input.maximumLength}
            value={actionValues[input.name] ?? ''} onChange={event => setActionValues(values => ({ ...values, [input.name]: event.target.value }))} />
        )}
        {transition.error && <div className="form-error" role="alert">{workflowError(transition.error, locale)}</div>}
        {actionRecord && <p role="status">{t('workflowState')}: {workflowText(actionRecord.workflowState, locale)}</p>}
        {refresh.error && <div className="form-error" role="alert">{workflowError(refresh.error, locale)}</div>}
        {isStaleConflict(transition.error) && actionRecord && <Button type="button" disabled={refresh.isPending} onClick={() => refresh.mutate(actionRecord.id)}>{t('refreshRecord')}</Button>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={closeAction}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={transition.isPending || isStaleConflict(transition.error) || !actionRecord?.availableActions.includes(selectedAction ?? '')}>{t(transition.isPending ? 'saving' : 'confirmAction')}</Button>
        </div>
      </form>
    </Dialog>
  </>
}

export const __MODULE_CAMEL__Module = defineWebModule({
  id: '__MODULE_ID__',
  name: __LABEL_PLURAL_EN__,
  version: '1.0.0',
  description: '__DESCRIPTION__',
  requires: [],
  optionalDependencies: [],
  routes: [{ id: '__MODULE_ID__.list', path: '/__RESOURCE__', component: __MODULE__Page }],
  navigation: [{ id: '__MODULE_ID__.navigation', section: 'Workspace', order: 50, to: '/__RESOURCE__', label: __LABEL_PLURAL_EN__, labels: __LABEL_PLURAL__,  icon: Boxes, requiredPermission: '__MODULE_ID__.read' }],
  extensionPoints: [__MODULE_CAMEL__Table],
  extensions: [],
  archiveResources: [{
    kind: '__MODULE_ID__',
    typeLabel: __LABEL_SINGULAR_EN__,
    readPermission: '__MODULE_ID__.read',
    managePermission: '__MODULE_ID__.manage',
    async load() {
      const records = await __MODULE_CAMEL__List({ lifecycle: 'recoverable' })
      return records.map((record) => ({ id: record.id, title: __WEB_DISPLAY_VALUE__, description: __WEB_DESCRIPTION_VALUE__, lifecycle: record.lifecycle as ArchiveLifecycle, version: record.version }))
    },
    restore: (id: string, expectedVersion?: string) => __MODULE_CAMEL__Restore(id, { expectedVersion: expectedVersion ?? '' }),
    requestDeletion: (id: string, reason: string, expectedVersion?: string) => __MODULE_CAMEL__RequestDeletion(id, { reason, expectedVersion: expectedVersion ?? '' }),
  }],
})
