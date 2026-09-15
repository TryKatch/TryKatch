import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, __MODULE_CAMEL__List, __MODULE_CAMEL__Create, __MODULE_CAMEL__Update, __MODULE_CAMEL__Archive, __MODULE_CAMEL__Restore, __MODULE_CAMEL__RequestDeletion, type __ENTITY__Dto } from '@__NPM_SCOPE__/api-client'
import { defineWebModule, type ArchiveLifecycle } from '@__NPM_SCOPE__/module-sdk'
import { Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Surface, type DataTableColumn, type RowAction } from '@__NPM_SCOPE__/ui'
import { Boxes, Plus } from 'lucide-react'
import { useState } from 'react'
import { workflowActions, runWorkflowAction, workflowText, workflowError, isStaleConflict } from './workflow'
import { use__MODULE__Messages } from './messages'
__WEB_DATETIME_IMPORT__

interface OrganizationAccess { permissions: string[] }
type LoadResult<T> = { value: T; failure?: never } | { value?: never; failure: string }

async function loadResult<T>(load: () => Promise<T>): Promise<LoadResult<T>> {
  try { return { value: await load() } }
  catch (error) { return { failure: error instanceof Error ? error.message : 'The request failed.' } }
}

export function __MODULE__Page() {
  const { t, tableLabels, locale } = use__MODULE__Messages()
  const queryClient = useQueryClient()
  const [actionRecord, setActionRecord] = useState<__ENTITY__Dto>()
  const [selectedAction, setSelectedAction] = useState<string>()
  const [actionValues, setActionValues] = useState<Record<string, string>>({})
  const actionDefinition = workflowActions.find(action => action.id === selectedAction)
  const [editing, setEditing] = useState<__ENTITY__Dto | null | undefined>(undefined)
  const [viewing, setViewing] = useState<__ENTITY__Dto>()
  __WEB_FIELD_STATE__
  const records = useQuery({
    queryKey: ['__MODULE_ID__s'],
    queryFn: () => loadResult(() => __MODULE_CAMEL__List({ lifecycle: 'active' })),
  })
  const access = useQuery({
    queryKey: ['access'],
    queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }),
  })
  const canManage = (access.data?.permissions ?? []).includes('__MODULE_ID__.manage')
  const closeEditor = () => { setEditing(undefined); __WEB_FIELD_RESET__ }
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
  const closeAction = () => { setSelectedAction(undefined); setActionRecord(undefined); setActionValues({}); transition.reset() }
  const openAction = (record: __ENTITY__Dto, id: string) => { transition.reset(); setActionRecord(record); setSelectedAction(id); setActionValues({}) }
  const openCreate = () => { save.reset(); setEditing(null); __WEB_FIELD_RESET__ }
  const openEdit = (record: __ENTITY__Dto) => { save.reset(); setEditing(record); __WEB_FIELD_EDIT__ }
  const refreshEditor = async () => {
    const result = await records.refetch()
    const latest = result.data?.value?.find(record => record.id === editing?.id)
    if (latest) { setEditing(latest); save.reset() }
  }
  const refreshAction = async () => {
    const result = await records.refetch()
    const latest = result.data?.value?.find(record => record.id === actionRecord?.id)
    if (latest) { setActionRecord(latest); transition.reset() }
  }
  const actionsFor = (record: __ENTITY__Dto): RowAction[] => [
    { label: t('view'), icon: 'view', onSelect: () => setViewing(record) },
    ...(canManage ? [
      { label: t('edit'), icon: 'edit', disabled: !record.canEdit, onSelect: () => openEdit(record) },
      { label: t('archive'), icon: 'archive', disabled: archive.isPending, onSelect: () => archive.mutate(record) },
    ] satisfies RowAction[] : []),
    ...workflowActions.filter(action => record.availableActions.includes(action.id)).map((action): RowAction => ({ label: workflowText(action.label, locale), icon: 'edit', disabled: transition.isPending, onSelect: () => openAction(record, action.id) })),
  ]
  const columns: DataTableColumn<__ENTITY__Dto>[] = [
    { id: 'workflowState', header: t('workflowState'), cell: record => workflowText(record.workflowState, locale) },
    __WEB_COLUMNS__
    { id: 'actions', header: '', cell: (record) => <RowActions label={t('actionsFor', { name: __WEB_DISPLAY_VALUE__ })} actions={actionsFor(record)} />, hideable: false, align: 'right', width: 54 },
  ]
  const failure = records.data?.failure ?? access.error?.message ?? records.error?.message
  const activeRecords = records.data?.value ?? []

  return <>
    <PageHeader eyebrow={t('application')} title={t('moduleTitle')} description={t('pageDescription')}
      actions={canManage && <Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('newRecord')}</Button>} />
    <Surface className="collection">
      {archive.error && <div className="page-alert" role="alert">{workflowError(archive.error, locale)}</div>}
      {records.isLoading || access.isLoading
        ? <p role="status">{t('loading')}</p>
        : failure
          ? <EmptyState title={t('loadFailed')} description={failure}
              action={<Button onClick={() => { records.refetch(); access.refetch() }}>{t('tryAgain')}</Button>} />
          : <DataTable labels={tableLabels} ariaLabel={t('moduleTitle')} data={activeRecords} columns={columns}
              getRowId={(record) => record.id} searchPlaceholder={t('search')}
              empty={<EmptyState title={t('emptyTitle')} description={t(canManage ? 'emptyManage' : 'emptyReadOnly')}
                action={canManage ? <Button variant="primary" onClick={openCreate}>{t('createRecord')}</Button> : undefined} />} />}
    </Surface>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && closeEditor()}
      title={t(editing ? 'editRecord' : 'createRecord')} description={t('editorDescription')}>
      <form className="dialog-form" onSubmit={(event) => { event.preventDefault(); save.mutate() }}>
        __WEB_FORM_FIELDS__
        {save.error && <div className="form-error" role="alert">{workflowError(save.error, locale)}</div>}
        {editing && <p role="status">{t('workflowState')}: {workflowText(editing.workflowState, locale)}</p>}
        {isStaleConflict(save.error) && <Button type="button" onClick={refreshEditor}>{t('refreshRecord')}</Button>}
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
        {actionDefinition?.inputs.map(input => <label key={input.name}>
          {workflowText(input.label, locale)}
          <textarea name={input.name} required={input.required} minLength={input.minimumLength ?? undefined} maxLength={input.maximumLength}
            value={actionValues[input.name] ?? ''} onChange={event => setActionValues(values => ({ ...values, [input.name]: event.target.value }))} />
        </label>)}
        {transition.error && <div className="form-error" role="alert">{workflowError(transition.error, locale)}</div>}
        {actionRecord && <p role="status">{t('workflowState')}: {workflowText(actionRecord.workflowState, locale)}</p>}
        {isStaleConflict(transition.error) && <Button type="button" onClick={refreshAction}>{t('refreshRecord')}</Button>}
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
  extensionPoints: [],
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
