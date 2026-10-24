import { skipToken, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch } from '@trykatch/api-client'
import { ModuleExtensionSlot, useModuleFeature, useModuleFeatures, useModuleI18n } from '@trykatch/module-sdk'
import { Badge, Button, DataTable, Dialog, EmptyState, FloatingInput, FloatingTextarea, PageHeader, RowActions, Skeleton, Surface, type DataTableColumn, type RowAction } from '@trykatch/ui'
import { Plus } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { projectSchema } from './validation'

interface RecordLifecycle { status: 'Active' | 'Archived' | 'Deleted'; archivedAt?: string | null; deletedAt?: string | null; deletionReason?: string | null }
function LifecycleBadge({ lifecycle }: { lifecycle: RecordLifecycle }) {
  const { t } = useModuleI18n()
  const tone = lifecycle.status === 'Active' ? 'success' : lifecycle.status === 'Archived' ? 'warning' : 'danger'
  return <Badge tone={tone}>{t(lifecycle.status === 'Deleted' ? 'Pending deletion' : lifecycle.status)}</Badge>
}

interface Project { id: string; name: string; description: string; createdAt: string; updatedAt?: string; lifecycle: RecordLifecycle }
interface ProjectPage { items: Project[]; page: number; pageSize: number; totalCount: number }
interface OrganizationAccess { organizationId: string; membershipId: string; permissions: string[] }
interface ProjectDraft { id?: string; name: string; description: string }

export function ProjectsPage() {
  const { organizationId } = useModuleFeatures()
  return <ProjectsPageContent key={organizationId ?? 'unresolved-organization'} />
}

function ProjectsPageContent() {
  const { t, formatDate } = useModuleI18n()
  const features = useModuleFeatures()
  const readsEnabled = useModuleFeature('projects.reads')
  const writesEnabled = useModuleFeature('projects.writes')
  const organizationId = features.organizationId
  const formatRecordDate = (value?: string | null) => value ? formatDate(value, { dateStyle: 'medium', timeStyle: 'short' }) : '—'
  const dataTableLabels = {
    searchTable: t('Search table'), result: t('result'), results: t('results'), columns: t('Columns'), tableSettings: t('Table settings'),
    closeTableSettings: t('Close table settings'), rowDensity: t('Row density'), compact: t('Compact'), comfortable: t('Comfortable'),
    spacious: t('Spacious'), required: t('Required'), details: t('Details'), noMatchingResults: t('No matching results.'),
    showDetails: (row: string) => t('Show details for {row}', { row }), hideDetails: (row: string) => t('Hide details for {row}', { row }),
    showing: (start: number, end: number, total: number) => t('Showing {start}–{end} of {total}', { start, end, total }),
    previous: t('Previous'), page: (page: number, count: number) => t('Page {page} of {count}', { page, count }), next: t('Next'),
  }
  const queryClient = useQueryClient()
  // Keep an unsaved draft in this session's organization-keyed memory while the
  // route is temporarily unmounted for a fresh policy check. No network or storage.
  const draft = useQuery<ProjectDraft | null>({
    queryKey: ['project-editor', organizationId], queryFn: skipToken, initialData: null, gcTime: 5 * 60_000,
  })
  const [editing, setEditingState] = useState<ProjectDraft | null>(draft.data ?? null)
  const setEditing = useCallback((value: ProjectDraft | null) => {
    setEditingState(value)
    queryClient.setQueryData(['project-editor', organizationId], value)
  }, [organizationId, queryClient])
  const [viewing, setViewing] = useState<Project>()
  const [error, setError] = useState<string>()
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const access = useQuery({
    queryKey: ['access', organizationId],
    queryFn: ({ signal }) => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET', signal }),
    enabled: Boolean(organizationId),
  })
  const currentAccess = organizationId !== undefined && access.isSuccess && access.data.organizationId === organizationId
    ? access.data : undefined
  const canRead = readsEnabled && currentAccess?.permissions.includes('projects.read') === true
  const canWrite = writesEnabled && currentAccess?.permissions.includes('projects.manage') === true
  const canSubmitWrite = useRef(canWrite)
  canSubmitWrite.current = canWrite
  const query = useQuery({
    queryKey: ['projects', organizationId, 'active'],
    queryFn: ({ signal }) => customFetch<ProjectPage>('/api/v1/projects?page=1&pageSize=100&lifecycle=active', { method: 'GET', signal }),
    enabled: canRead,
  })
  const accessLoading = !organizationId || access.isLoading || (access.data !== undefined && currentAccess === undefined && access.isFetching)
  useEffect(() => {
    const denied = features.status === 'error' || features.status === 'success' && (!writesEnabled
      || access.isError || access.isSuccess && (!currentAccess || !currentAccess.permissions.includes('projects.manage')))
    if (denied) setEditing(null)
  }, [access.isError, access.isSuccess, currentAccess, features.status, setEditing, writesEnabled])
  useEffect(() => {
    if (!canRead) setViewing(undefined)
  }, [canRead])
  const save = useMutation({
    mutationFn: (input: { id?: string; name: string; description: string }) => {
      if (!canSubmitWrite.current) throw new Error(t('Projects are unavailable'))
      return customFetch<Project>(
        input.id ? `/api/v1/projects/${input.id}` : '/api/v1/projects',
        { method: input.id ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) },
      )
    },
    onSuccess: async () => { setEditing(null); await queryClient.invalidateQueries({ queryKey: ['projects'] }) },
    onError: (reason) => setError(reason instanceof Error ? reason.message : t('Unable to save project')),
  })
  const changeLifecycle = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'archive' | 'restore' }) => {
      if (!canSubmitWrite.current) throw new Error(t('Projects are unavailable'))
      return customFetch<void>(`/api/v1/projects/${id}/${action}`, { method: 'POST' })
    },
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
  })

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canSubmitWrite.current || save.isPending) return
    setError(undefined)
    const form = new FormData(event.currentTarget)
    const result = projectSchema.safeParse({ name: String(form.get('name') ?? ''), description: String(form.get('description') ?? '') })
    if (!result.success) {
      setFieldErrors(Object.fromEntries(result.error.issues.map((issue) => [String(issue.path[0]), issue.message])))
      return
    }
    setFieldErrors({})
    save.mutate({ id: editing?.id, ...result.data })
  }

  const openCreate = () => {
    if (!canSubmitWrite.current) return
    setError(undefined); setFieldErrors({}); setEditing({ name: '', description: '' })
  }
  const actionsFor = (project: Project): RowAction[] => [
    { label: t('View'), icon: 'view', onSelect: () => canRead && setViewing(project) },
    ...(canWrite ? [
      { label: t('Edit'), icon: 'edit' as const, onSelect: () => {
        if (!canSubmitWrite.current) return
        setError(undefined); setFieldErrors({}); setEditing({ id: project.id, name: project.name, description: project.description })
      } },
      { label: t('Archive'), icon: 'archive' as const, onSelect: () => changeLifecycle.mutate({ id: project.id, action: 'archive' }) },
    ] : []),
  ]
  const columns: DataTableColumn<Project>[] = [
    { id: 'name', header: t('Project'), cell: (project) => <div><strong>{project.name}</strong><small>{project.description}</small></div>, sortValue: (project) => project.name, searchValue: (project) => `${project.name} ${project.description}`, hideable: false },
    { id: 'status', header: t('Status'), cell: (project) => <LifecycleBadge lifecycle={project.lifecycle} />, sortValue: (project) => project.lifecycle.status },
    { id: 'created', header: t('Created'), cell: (project) => formatDate(project.createdAt), sortValue: (project) => new Date(project.createdAt) },
    { id: 'actions', header: t('Actions'), cell: (project) => <RowActions label={`${t('Actions for')} ${project.name}`} actions={actionsFor(project)} />, hideable: false, align: 'right', width: 90 },
  ]
  return <>
    <PageHeader eyebrow={t('Application')} title={t('Projects')} description={t('Create, manage, archive, and recover organization-scoped projects.')} actions={canWrite && <Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('New project')}</Button>} />
    <Surface className="collection">
      {features.status === 'loading' || (readsEnabled && !currentAccess && accessLoading)
        ? <div role="status" aria-label={t('Checking feature availability')} className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div>
        : !readsEnabled || features.status === 'error'
          ? <EmptyState headingLevel={2} title={t(features.status === 'error' ? 'Feature availability could not be checked' : 'Projects are unavailable')} description={t(features.status === 'error' ? 'Try again shortly.' : 'This feature is unavailable in this organization.')} />
          : !currentAccess || !currentAccess.permissions.includes('projects.read')
            ? <EmptyState headingLevel={2} title={t('Project access unavailable')} description={t('You do not have permission to view projects.')} />
            : query.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div>
              : query.isError ? <EmptyState headingLevel={2} title={t('Projects could not be loaded')} description={query.error.message} action={<Button onClick={() => query.refetch()}>{t('Try again')}</Button>} />
                : <DataTable labels={dataTableLabels} ariaLabel={t('Projects')} data={query.data?.items ?? []} columns={columns} getRowId={(project) => project.id} searchPlaceholder={t('Search projects…')} initialSort={{ id: 'created', direction: 'desc' }} empty={<EmptyState headingLevel={2} title={t('No projects')} description={t('Create the first project to exercise organization-scoped RLS.')} action={canWrite && <Button variant="primary" onClick={openCreate}>{t('Create project')}</Button>} />} />}
    </Surface>
    {canRead && currentAccess && <ModuleExtensionSlot point="projects.list.after-table" context={{ resultCount: query.data?.items.length ?? 0 }} permissions={currentAccess.permissions} />}
    <Dialog open={canWrite && editing !== null} onOpenChange={(open) => !open && setEditing(null)} title={t(editing?.id ? 'Edit project' : 'Create project')} description={t('Changes are authorized in the application layer and isolated by PostgreSQL RLS.')}>
      <form className="dialog-form" onSubmit={submit} noValidate aria-busy={save.isPending}>
        <FloatingInput label={t('Name')} name="name" value={editing?.name ?? ''} maxLength={120} required autoFocus disabled={save.isPending || !canWrite} error={fieldErrors.name ? t(fieldErrors.name) : undefined} onChange={(event) => { if (editing) setEditing({ ...editing, name: event.target.value }); setFieldErrors((current) => ({ ...current, name: '' })) }} />
        <FloatingTextarea label={t('Description')} name="description" value={editing?.description ?? ''} maxLength={2000} rows={5} disabled={save.isPending || !canWrite} error={fieldErrors.description ? t(fieldErrors.description) : undefined} onChange={(event) => { if (editing) setEditing({ ...editing, description: event.target.value }); setFieldErrors((current) => ({ ...current, description: '' })) }} />
        {error && <div className="form-error" role="alert">{error}</div>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={() => setEditing(null)}>{t('Cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending || !canWrite}>{t(save.isPending ? 'Saving…' : 'Save project')}</Button>
        </div>
      </form>
    </Dialog>
    <Dialog open={canRead && viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.name ?? t('Project')} description={t('Project details and record lifecycle.')}>
      {canRead && viewing && <dl className="record-details">
        <div><dt>{t('Status')}</dt><dd><LifecycleBadge lifecycle={viewing.lifecycle} /></dd></div>
        <div><dt>{t('Description')}</dt><dd>{viewing.description || t('No description')}</dd></div>
        <div><dt>{t('Created')}</dt><dd>{formatRecordDate(viewing.createdAt)}</dd></div>
        <div><dt>{t('Updated')}</dt><dd>{formatRecordDate(viewing.updatedAt)}</dd></div>
      </dl>}
    </Dialog>
  </>
}
