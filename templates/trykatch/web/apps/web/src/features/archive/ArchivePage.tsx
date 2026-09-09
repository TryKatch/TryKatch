import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch } from '@trykatchapp/api-client'
import { Badge, Button, DataTable, DeleteConfirmationDialog, EmptyState, PageHeader, RowActions, Skeleton, Surface, type DataTableColumn } from '@trykatchapp/ui'
import { ArchiveRestore, FolderKanban, Mail, RefreshCw, ShieldCheck, UserRound } from 'lucide-react'
import { useState } from 'react'
import { formatRecordDate, LifecycleBadge, RecordDetailsDialog } from '../../components/RecordLifecycle'
import {
  archiveResourceDefinitions,
  archiveTimestamp,
  canRequestArchiveItemDeletion,
  canRestoreArchiveItem,
  loadArchiveItems,
  requestArchiveItemDeletion,
  restoreArchiveItem,
  type ArchiveItem,
  type ArchiveResourceKind,
} from './archiveResources'

interface OrganizationAccess { membershipId: string; permissions: string[] }
type StateFilter = 'all' | 'Archived' | 'Deleted'

const resourceIcons = {
  project: FolderKanban,
  member: UserRound,
  invitation: Mail,
  role: ShieldCheck,
} as const

export function ArchivePage() {
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
        queryClient.invalidateQueries({ queryKey: [item.kind === 'project' ? 'projects' : item.kind === 'member' ? 'members' : item.kind === 'invitation' ? 'invitations' : 'access-levels'] }),
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
        queryClient.invalidateQueries({ queryKey: [item.kind === 'project' ? 'projects' : item.kind === 'member' ? 'members' : item.kind === 'invitation' ? 'invitations' : 'access-levels'] }),
      ])
    },
  })

  const permissions = access.data?.permissions ?? []
  const visibleResourceTypes = archiveResourceDefinitions.filter((definition) => permissions.includes(definition.readPermission))
  const items = (archive.data ?? []).filter((item) =>
    (resourceKind === 'all' || item.kind === resourceKind) &&
    (recordState === 'all' || item.lifecycle.status === recordState))
  const archivedCount = archive.data?.filter((item) => item.lifecycle.status === 'Archived').length ?? 0
  const pendingDeletionCount = archive.data?.filter((item) => item.lifecycle.status === 'Deleted').length ?? 0

  const columns: DataTableColumn<ArchiveItem>[] = [
    {
      id: 'record',
      header: 'Record',
      hideable: false,
      cell: (item) => {
        const Icon = resourceIcons[item.kind]
        return <div className="archive-resource-cell"><span><Icon size={16} /></span><div><strong>{item.title}</strong><small>{item.description}</small></div></div>
      },
      sortValue: (item) => item.title,
      searchValue: (item) => `${item.title} ${item.description} ${item.typeLabel} ${item.lifecycle.deletionReason ?? ''}`,
    },
    { id: 'type', header: 'Type', cell: (item) => <Badge>{item.typeLabel}</Badge>, sortValue: (item) => item.typeLabel },
    { id: 'state', header: 'State', cell: (item) => <LifecycleBadge lifecycle={item.lifecycle} />, sortValue: (item) => item.lifecycle.status },
    { id: 'removed', header: 'Removed', cell: (item) => formatRecordDate(item.lifecycle.deletedAt ?? item.lifecycle.archivedAt), sortValue: archiveTimestamp },
    { id: 'reason', header: 'Reason', defaultVisible: false, cell: (item) => item.lifecycle.deletionReason ?? 'Archived without a deletion reason', searchValue: (item) => item.lifecycle.deletionReason ?? '' },
    {
      id: 'actions', header: '', hideable: false, align: 'right', width: 54,
      cell: (item) => <RowActions label={`Actions for ${item.title}`} actions={[
        { label: 'View', icon: 'view', onSelect: () => setViewing(item) },
        ...(canRestoreArchiveItem(item, permissions) ? [{ label: 'Restore', icon: 'restore' as const, onSelect: () => restore.mutate(item), disabled: restore.isPending }] : []),
        ...(canRequestArchiveItemDeletion(item, permissions) ? [{ label: 'Delete', icon: 'delete' as const, danger: true, onSelect: () => setDeleting(item), disabled: requestDeletion.isPending }] : []),
      ]} />,
    },
  ]

  const loadError = access.error ?? archive.error
  return <>
    <PageHeader eyebrow="Recovery" title="Archive" description="Review and restore records removed from active work." actions={<Button variant="secondary" onClick={() => archive.refetch()} disabled={archive.isFetching}><RefreshCw size={14} /> {archive.isFetching ? 'Refreshing…' : 'Refresh'}</Button>} />
    <Surface className="archive-intro">
      <span className="archive-intro-icon"><ArchiveRestore size={19} /></span>
      <div><strong>A safe place for recoverable records</strong><p>Archived records are intentionally retained but inactive. Pending-deletion records include an accountability reason and remain restorable until an approved retention policy permits permanent removal.</p></div>
    </Surface>
    <div className="archive-summary-grid" aria-label="Archive summary">
      <Surface><span>Recoverable</span><strong>{archive.data?.length ?? 0}</strong><small>All removed records</small></Surface>
      <Surface><span>Archived</span><strong>{archivedCount}</strong><small>Intentionally retired</small></Surface>
      <Surface><span>Pending deletion</span><strong>{pendingDeletionCount}</strong><small>Awaiting retention policy</small></Surface>
    </div>
    <Surface className="collection">
      {archive.isLoading || access.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div>
        : loadError ? <EmptyState title="Archive could not be loaded" description={loadError.message} action={<Button onClick={() => archive.refetch()}>Try again</Button>} />
          : <DataTable
            ariaLabel="Recoverable records"
            data={items}
            columns={columns}
            getRowId={(item) => `${item.kind}-${item.id}`}
            searchPlaceholder="Search archive…"
            initialSort={{ id: 'removed', direction: 'desc' }}
            pageSize={25}
            toolbar={<><select aria-label="Filter by resource type" value={resourceKind} onChange={(event) => setResourceKind(event.target.value as typeof resourceKind)}><option value="all">All record types</option>{visibleResourceTypes.map((definition) => <option value={definition.kind} key={definition.kind}>{definition.typeLabel}</option>)}</select><select aria-label="Filter by recovery state" value={recordState} onChange={(event) => setRecordState(event.target.value as StateFilter)}><option value="all">All recovery states</option><option value="Archived">Archived</option><option value="Deleted">Pending deletion</option></select></>}
            empty={<EmptyState title="Nothing in the archive" description="Records that are archived or recoverably deleted will appear here." />}
          />}
    </Surface>
    {(restore.error || requestDeletion.error) && <div className="page-alert" role="alert">{(restore.error ?? requestDeletion.error)?.message}</div>}
    <RecordDetailsDialog
      open={viewing !== undefined}
      onOpenChange={(open) => !open && setViewing(undefined)}
      title={viewing?.title ?? 'Archived record'}
      description="Recovery details and removal context."
      recordType={viewing?.typeLabel}
      status={viewing ? <LifecycleBadge lifecycle={viewing.lifecycle} /> : undefined}
      details={viewing ? [
        { label: 'Resource type', value: viewing.typeLabel },
        { label: 'Current state', value: <LifecycleBadge lifecycle={viewing.lifecycle} /> },
        { label: 'Removed', value: formatRecordDate(viewing.lifecycle.deletedAt ?? viewing.lifecycle.archivedAt) },
        { label: 'Reason', value: viewing.lifecycle.deletionReason ?? 'Archived without a deletion reason' },
        { label: 'Record identifier', value: viewing.id },
      ] : []}
      actions={viewing && canRestoreArchiveItem(viewing, permissions) ? <><Button variant="primary" disabled={restore.isPending} onClick={() => restore.mutate(viewing)}><ArchiveRestore size={14} /> {restore.isPending ? 'Restoring…' : 'Restore record'}</Button>{canRequestArchiveItemDeletion(viewing, permissions) && <Button variant="danger" onClick={() => setDeleting(viewing)}>Delete</Button>}</> : undefined}
    />
    <DeleteConfirmationDialog open={deleting !== undefined} onOpenChange={(open) => !open && setDeleting(undefined)} recordType={deleting?.typeLabel.toLowerCase() ?? 'record'} recordName={deleting?.title ?? ''} isDeleting={requestDeletion.isPending} error={requestDeletion.error?.message} onConfirm={(reason) => deleting && requestDeletion.mutate({ item: deleting, reason })} />
  </>
}
