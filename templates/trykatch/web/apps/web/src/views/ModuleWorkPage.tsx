import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, moduleWorkList, moduleWorkPause, moduleWorkResume, moduleWorkRetry, type ModuleWorkActionRequest, type ModuleWorkView } from '@trykatch/api-client'
import { Badge, Button, DataTable, Dialog, EmptyState, FloatingSelect, PageHeader, RowActions, type DataTableColumn } from '@trykatch/ui'
import { useMemo, useState } from 'react'
import { useI18n } from '../i18n/I18nProvider'

interface PlatformSession { isPlatformAdministrator?: boolean; platformPermissions?: string[] }
type WorkAction = 'pause' | 'resume' | 'retry'

const PAGE_SIZE = 50
const ALL_STATES = '__all__'
const listKey = ['platform', 'module-work'] as const

function statusTone(work: ModuleWorkView): 'neutral' | 'success' | 'warning' | 'danger' | 'info' {
  if (work.operatorPaused) return 'warning'
  if (work.state.toLowerCase() === 'completed') return 'success'
  if (['failed', 'denied', 'rejected', 'terminal'].includes(work.state.toLowerCase())) return 'danger'
  if (['running', 'leased'].includes(work.state.toLowerCase())) return 'info'
  return 'neutral'
}

function errorMessage(error: unknown, t: (message: string) => string) {
  return (error as { status?: number } | undefined)?.status === 409
    ? t('Work item changed. Reload the list before trying again.')
    : t('The requested work action could not be completed.')
}

export function ModuleWorkPage() {
  const { t, formatDate } = useI18n()
  const queryClient = useQueryClient()
  const [page, setPage] = useState(1)
  const [state, setState] = useState('')
  const [pendingAction, setPendingAction] = useState<{ work: ModuleWorkView; action: WorkAction }>()
  const [success, setSuccess] = useState('')
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<PlatformSession>('/api/v1/auth/session', { method: 'GET' }), retry: false })
  const canRead = session.data?.isPlatformAdministrator === true || session.data?.platformPermissions?.includes('platform.jobs.read') === true
  const canManage = session.data?.isPlatformAdministrator === true || session.data?.platformPermissions?.includes('platform.jobs.manage') === true
  const accessError = session.isError
  const work = useQuery({
    queryKey: [...listKey, page, state],
    enabled: canRead,
    queryFn: () => moduleWorkList({ page, pageSize: PAGE_SIZE, state: state || undefined }),
  })
  const change = useMutation({
    mutationFn: ({ item, action }: { item: ModuleWorkView; action: WorkAction }) => {
      const request: ModuleWorkActionRequest = { requestId: crypto.randomUUID(), expectedGeneration: Number(item.recoveryGeneration) }
      if (action === 'pause') return moduleWorkPause(item.workId, request)
      if (action === 'resume') return moduleWorkResume(item.workId, request)
      return moduleWorkRetry(item.workId, request)
    },
    onSuccess: async (_receipt, variables) => {
      setPendingAction(undefined)
      setSuccess(t(variables.action === 'pause' ? 'Work item paused.' : variables.action === 'resume' ? 'Work item resumed.' : 'Work item queued for retry.'))
      await queryClient.invalidateQueries({ queryKey: listKey })
    },
    onError: () => setPendingAction(undefined),
  })

  const columns = useMemo<DataTableColumn<ModuleWorkView>[]>(() => [
    { id: 'job', header: t('Job'), hideable: false, cell: (item) => <div><strong>{item.jobKey}</strong><small>{t('Version {version}', { version: item.jobVersion })}</small></div>, sortValue: (item) => item.jobKey, searchValue: (item) => `${item.jobKey} ${item.workId} ${item.organizationId}` },
    { id: 'organization', header: t('Organization'), cell: (item) => <code className="event-key">{item.organizationId}</code>, sortValue: (item) => item.organizationId },
    { id: 'state', header: t('Status'), cell: (item) => <Badge tone={statusTone(item)}>{t(item.operatorPaused ? 'Paused by operator' : item.state)}</Badge>, sortValue: (item) => item.operatorPaused ? 'Paused' : item.state },
    { id: 'attempts', header: t('Attempts'), align: 'right', cell: (item) => item.attempts, sortValue: (item) => item.attempts },
    { id: 'created', header: t('Created'), cell: (item) => formatDate(item.createdAtUtc, { dateStyle: 'medium', timeStyle: 'short' }), sortValue: (item) => new Date(item.createdAtUtc) },
    { id: 'due', header: t('Due'), cell: (item) => item.dueAtUtc ? formatDate(item.dueAtUtc, { dateStyle: 'medium', timeStyle: 'short' }) : t('Immediate'), sortValue: (item) => item.dueAtUtc ? new Date(item.dueAtUtc) : '' },
    { id: 'nextAttempt', header: t('Next attempt'), defaultVisible: false, cell: (item) => item.nextAttemptAtUtc ? formatDate(item.nextAttemptAtUtc, { dateStyle: 'medium', timeStyle: 'short' }) : '—', sortValue: (item) => item.nextAttemptAtUtc ? new Date(item.nextAttemptAtUtc) : '' },
    { id: 'failure', header: t('Failure code'), defaultVisible: false, cell: (item) => item.failureCode ?? '—', sortValue: (item) => item.failureCode ?? '' },
    ...(canManage ? [{ id: 'actions', header: t('Actions'), hideable: false as const, align: 'right' as const, width: 54, cell: (item: ModuleWorkView) => <RowActions label={t('Actions for {job}', { job: item.jobKey })} actions={[
      ...(!item.operatorPaused && ['queued', 'paused'].includes(item.state.toLowerCase()) ? [{ label: t('Pause'), icon: 'archive' as const, onSelect: () => setPendingAction({ work: item, action: 'pause' }) }] : []),
      ...(item.operatorPaused ? [{ label: t('Resume'), icon: 'restore' as const, onSelect: () => setPendingAction({ work: item, action: 'resume' }) }] : []),
      ...(!item.operatorPaused && ['terminal', 'denied'].includes(item.state.toLowerCase()) ? [{ label: t('Retry'), icon: 'restore' as const, onSelect: () => setPendingAction({ work: item, action: 'retry' }) }] : []),
    ]} /> }] : []),
  ], [canManage, formatDate, t])

  const totalCount = Number(work.data?.totalCount ?? 0)
  const pageCount = Math.max(1, Math.ceil(totalCount / PAGE_SIZE))
  const actionName = pendingAction ? t(pendingAction.action === 'pause' ? 'Pause' : pendingAction.action === 'resume' ? 'Resume' : 'Retry') : ''

  return <>
    <PageHeader eyebrow={t('Platform administration')} title={t('Platform work')} description={t('Review and control scheduled and queued platform work.')} />
    {accessError && <div className="form-error" role="alert">{t('Platform access could not be checked.')}</div>}
    {!accessError && !session.isLoading && !canRead && <div className="form-error" role="alert">{t('You do not have permission to view platform work.')}</div>}
    {canRead && <>
      {!canManage && <p role="status">{t('Read-only access')}</p>}
      {work.isError && <div className="form-error" role="alert">{t('Platform work could not be loaded.')} <Button variant="ghost" onClick={() => void work.refetch()}>{t('Retry loading')}</Button></div>}
      {change.error && <div className="form-error" role="alert">{errorMessage(change.error, t)}{(change.error as { status?: number }).status === 409 && <Button variant="ghost" onClick={() => { change.reset(); setPendingAction(undefined); void work.refetch() }}>{t('Reload list')}</Button>}</div>}
      {success && <p className="form-success" role="status">{success}</p>}
      {work.isLoading ? <div className="shell-loading" role="status" aria-label={t('Loading platform work')}><span className="skeleton" /><span className="skeleton" /><span className="skeleton" /></div> : work.data && <>
        <div className="filter-bar"><FloatingSelect label={t('Filter by status')} value={state || ALL_STATES} onValueChange={(value) => { setState(value === ALL_STATES ? '' : value); setPage(1); setSuccess('') }} options={[{ value: ALL_STATES, label: t('All statuses') }, ...['queued', 'leased', 'paused', 'completed', 'denied', 'terminal'].map((value) => ({ value, label: t(value) }))]} /></div>
        {work.data.items.length === 0 && <h2 className="sr-only">{t('Platform work items')}</h2>}
        <DataTable ariaLabel={t('Platform work')} data={work.data.items} columns={columns} getRowId={(item) => item.workId} initialSort={{ id: 'created', direction: 'desc' }} empty={<EmptyState title={t('No platform work yet')} description={t('Queued and scheduled jobs will appear here.')} />} />
        <div className="table-pagination"><span>{t('Showing {start}–{end} of {total} work items', { start: totalCount ? (page - 1) * PAGE_SIZE + 1 : 0, end: Math.min(page * PAGE_SIZE, totalCount), total: totalCount })}</span><div><Button variant="secondary" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>{t('Previous')}</Button><span>{t('Page {page} of {count}', { page, count: pageCount })}</span><Button variant="secondary" disabled={page >= pageCount} onClick={() => setPage((value) => value + 1)}>{t('Next')}</Button></div></div>
      </>}
    </>}
    <Dialog open={Boolean(pendingAction)} onOpenChange={(open) => { if (!open) setPendingAction(undefined) }} title={t('Confirm {action}', { action: actionName.toLowerCase() })} description={pendingAction ? t(pendingAction.action === 'pause' ? 'Pause this work item? New attempts will wait until it is resumed.' : pendingAction.action === 'resume' ? 'Resume this work item?' : 'Queue this work item for another attempt?') : undefined}>
      {pendingAction && <div className="dialog-actions"><Button variant="ghost" disabled={change.isPending} onClick={() => setPendingAction(undefined)}>{t('Cancel')}</Button><Button variant={pendingAction.action === 'pause' ? 'danger' : 'primary'} disabled={change.isPending} onClick={() => { setSuccess(''); change.mutate({ item: pendingAction.work, action: pendingAction.action }) }}>{t('Confirm {action}', { action: actionName.toLowerCase() })}</Button></div>}
    </Dialog>
  </>
}
