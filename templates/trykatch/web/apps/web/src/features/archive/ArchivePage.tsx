import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch } from '@trykatchapp/api-client'
import { Badge, Button, DataTable, DeleteConfirmationDialog, EmptyState, PageHeader, RowActions, Skeleton, Surface, type DataTableColumn } from '@trykatchapp/ui'
import { ArchiveRestore, FolderKanban, Mail, RefreshCw, ShieldCheck, UserRound } from 'lucide-react'
import { useState } from 'react'
import { formatRecordDate, LifecycleBadge, RecordDetailsDialog } from '../../components/RecordLifecycle'
import {
  allArchiveResourceDefinitions,
  archiveTimestamp,
  canRequestArchiveItemDeletion,
  canRestoreArchiveItem,
  loadArchiveItems,
  requestArchiveItemDeletion,
  restoreArchiveItem,
  type ArchiveItem,
  type ArchiveResourceKind,
} from './archiveResources'
import { useDataTableLabels } from '../../i18n/useDataTableLabels'
import { useDeleteConfirmationLabels } from '../../i18n/useDeleteConfirmationLabels'
import { useI18n } from '../../i18n/I18nProvider'

interface OrganizationAccess { membershipId: string; permissions: string[] }
type StateFilter = 'all' | 'Archived' | 'Deleted'

const resourceIcons = {
  project: FolderKanban,
  member: UserRound,
  invitation: Mail,
  role: ShieldCheck,
} as const

function collectionQueryKey(kind: ArchiveResourceKind) {
  if (kind === 'project') return 'projects'
  if (kind === 'member') return 'members'
  if (kind === 'invitation') return 'invitations'
  if (kind === 'role') return 'access-levels'
  return `${kind}s`
}

export function ArchivePage() {
  const { t } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const deleteConfirmationLabels = useDeleteConfirmationLabels()
  const queryClient = useQueryClient()
  const [resourceKind, setResourceKind] = useState<'all' | ArchiveResourceKind>('all')
  const [recordState, setRecordState] = useState<StateFilter>('all')
  const [viewing, setViewing] = useState<ArchiveItem>()
  const [deleting, setDeleting] = useState<ArchiveItem>()
  const access = useQuery({
    queryKey: ['access'],
    queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }),
  })
  const archive = useQuery({
    queryKey: ['archive', access.data?.permissions],
    queryFn: () => loadArchiveItems(access.data?.permissions ?? []),
    enabled: access.isSuccess,
  })
  const restore = useMutation({
    mutationFn: (item: ArchiveItem) => restoreArchiveItem(item),
    onSuccess: async (_, item) => {
      setViewing(undefined)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['archive'] }),
        queryClient.invalidateQueries({ queryKey: [collectionQueryKey(item.kind)] }),
      ])
    },
  })
  const requestDeletion = useMutation({
    mutationFn: ({ item, reason }: { item: ArchiveItem; reason: string }) => requestArchiveItemDeletion(item, reason),
    onSuccess: async (_, { item }) => {
      setDeleting(undefined)
      setViewing(undefined)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['archive'] }),
        queryClient.invalidateQueries({ queryKey: [collectionQueryKey(item.kind)] }),
      ])
    },
  })

  const permissions = access.data?.permissions ?? []
  const visibleResourceTypes = allArchiveResourceDefinitions.filter((definition) => permissions.includes(definition.readPermission))
  const items = (archive.data ?? []).filter((item) =>
    (resourceKind === 'all' || item.kind === resourceKind) &&
    (recordState === 'all' || item.lifecycle.status === recordState))
  const archivedCount = archive.data?.filter((item) => item.lifecycle.status === 'Archived').length ?? 0
  const pendingDeletionCount = archive.data?.filter((item) => item.lifecycle.status === 'Deleted').length ?? 0

  const columns: DataTableColumn<ArchiveItem>[] = [
    {
      id: 'record',
      header: t('Record'),
      hideable: false,
      cell: (item) => {
        const Icon = resourceIcons[item.kind as keyof typeof resourceIcons] ?? FolderKanban
        return <div className="archive-resource-cell"><span><Icon size={16} /></span><div><strong>{item.title}</strong><small>{item.description}</small></div></div>
      },
      sortValue: (item) => item.title,
      searchValue: (item) => `${item.title} ${item.description} ${item.typeLabel} ${item.lifecycle.deletionReason ?? ''}`,
    },
    { id: 'type', header: t('Type'), cell: (item) => <Badge>{t(item.typeLabel)}</Badge>, sortValue: (item) => item.typeLabel },
    { id: 'state', header: t('State'), cell: (item) => <LifecycleBadge lifecycle={item.lifecycle} />, sortValue: (item) => item.lifecycle.status },
    { id: 'removed', header: t('Removed'), cell: (item) => formatRecordDate(item.lifecycle.deletedAt ?? item.lifecycle.archivedAt), sortValue: archiveTimestamp },
    { id: 'reason', header: t('Reason'), defaultVisible: false, cell: (item) => item.lifecycle.deletionReason ?? t('Archived without a deletion reason'), searchValue: (item) => item.lifecycle.deletionReason ?? '' },
    {
      id: 'actions', header: '', hideable: false, align: 'right', width: 54,
      cell: (item) => <RowActions label={t('Actions for {name}', { name: item.title })} actions={[
        { label: t('View'), icon: 'view', onSelect: () => setViewing(item) },
        ...(canRestoreArchiveItem(item, permissions) ? [{ label: t('Restore'), icon: 'restore' as const, onSelect: () => restore.mutate(item), disabled: restore.isPending }] : []),
        ...(canRequestArchiveItemDeletion(item, permissions) ? [{ label: t('Delete'), icon: 'delete' as const, danger: true, onSelect: () => setDeleting(item), disabled: requestDeletion.isPending }] : []),
      ]} />,
    },
  ]

  const loadError = access.error ?? archive.error
  return <>
    <PageHeader eyebrow={t('Recovery')} title={t('Archive')} description={t('Review and restore records removed from active work.')} actions={<Button variant="secondary" onClick={() => archive.refetch()} disabled={archive.isFetching}><RefreshCw size={14} /> {t(archive.isFetching ? 'Refreshing…' : 'Refresh')}</Button>} />
    <Surface className="archive-intro">
      <span className="archive-intro-icon"><ArchiveRestore size={19} /></span>
      <div><strong>{t('A safe place for recoverable records')}</strong><p>{t('Archived records are intentionally retained but inactive. Pending-deletion records include an accountability reason and remain restorable until an approved retention policy permits permanent removal.')}</p></div>
    </Surface>
    <div className="archive-summary-grid" aria-label={t('Archive summary')}>
      <Surface><span>{t('Recoverable')}</span><strong>{archive.data?.length ?? 0}</strong><small>{t('All removed records')}</small></Surface>
      <Surface><span>{t('Archived')}</span><strong>{archivedCount}</strong><small>{t('Intentionally retired')}</small></Surface>
      <Surface><span>{t('Pending deletion')}</span><strong>{pendingDeletionCount}</strong><small>{t('Awaiting retention policy')}</small></Surface>
    </div>
    <Surface className="collection">
      {archive.isLoading || access.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div>
        : loadError ? <EmptyState title={t('Archive could not be loaded')} description={loadError.message} action={<Button onClick={() => archive.refetch()}>{t('Try again')}</Button>} />
          : <DataTable
            labels={dataTableLabels}
            ariaLabel={t('Recoverable records')}
            data={items}
            columns={columns}
            getRowId={(item) => `${item.kind}-${item.id}`}
            searchPlaceholder={t('Search archive…')}
            initialSort={{ id: 'removed', direction: 'desc' }}
            pageSize={25}
            toolbar={<><select aria-label={t('Filter by resource type')} value={resourceKind} onChange={(event) => setResourceKind(event.target.value as typeof resourceKind)}><option value="all">{t('All record types')}</option>{visibleResourceTypes.map((definition) => <option value={definition.kind} key={definition.kind}>{t(definition.typeLabel)}</option>)}</select><select aria-label={t('Filter by recovery state')} value={recordState} onChange={(event) => setRecordState(event.target.value as StateFilter)}><option value="all">{t('All recovery states')}</option><option value="Archived">{t('Archived')}</option><option value="Deleted">{t('Pending deletion')}</option></select></>}
            empty={<EmptyState title={t('Nothing in the archive')} description={t('Records that are archived or recoverably deleted will appear here.')} />}
          />}
    </Surface>
    {(restore.error || requestDeletion.error) && <div className="page-alert" role="alert">{(restore.error ?? requestDeletion.error)?.message}</div>}
    <RecordDetailsDialog
      open={viewing !== undefined}
      onOpenChange={(open) => !open && setViewing(undefined)}
      title={viewing?.title ?? t('Archived record')}
      description={t('Recovery details and removal context.')}
      recordType={viewing ? t(viewing.typeLabel) : undefined}
      status={viewing ? <LifecycleBadge lifecycle={viewing.lifecycle} /> : undefined}
      details={viewing ? [
        { label: t('Resource type'), value: t(viewing.typeLabel) },
        { label: t('Current state'), value: <LifecycleBadge lifecycle={viewing.lifecycle} /> },
        { label: t('Removed'), value: formatRecordDate(viewing.lifecycle.deletedAt ?? viewing.lifecycle.archivedAt) },
        { label: t('Reason'), value: viewing.lifecycle.deletionReason ?? t('Archived without a deletion reason') },
        { label: t('Record identifier'), value: viewing.id },
      ] : []}
      actions={viewing && canRestoreArchiveItem(viewing, permissions) ? <><Button variant="primary" disabled={restore.isPending} onClick={() => restore.mutate(viewing)}><ArchiveRestore size={14} /> {t(restore.isPending ? 'Restoring…' : 'Restore record')}</Button>{canRequestArchiveItemDeletion(viewing, permissions) && <Button variant="danger" onClick={() => setDeleting(viewing)}>{t('Delete')}</Button>}</> : undefined}
    />
    <DeleteConfirmationDialog open={deleting !== undefined} onOpenChange={(open) => !open && setDeleting(undefined)} recordType={t(deleting?.typeLabel.toLowerCase() ?? 'record')} recordName={deleting?.title ?? ''} isDeleting={requestDeletion.isPending} error={requestDeletion.error?.message} labels={deleteConfirmationLabels} onConfirm={(reason) => deleting && requestDeletion.mutate({ item: deleting, reason })} />
  </>
}
