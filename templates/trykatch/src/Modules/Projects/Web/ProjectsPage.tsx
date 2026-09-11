import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch } from '@trykatch/api-client'
import { ModuleExtensionSlot, useModuleI18n } from '@trykatch/module-sdk'
import { Badge, Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Skeleton, Surface, type DataTableColumn, type RowAction } from '@trykatch/ui'
import { Plus } from 'lucide-react'
import { useState, type FormEvent } from 'react'

interface RecordLifecycle { status: 'Active' | 'Archived' | 'Deleted'; archivedAt?: string | null; deletedAt?: string | null; deletionReason?: string | null }
function LifecycleBadge({ lifecycle }: { lifecycle: RecordLifecycle }) {
  const { t } = useModuleI18n()
  const tone = lifecycle.status === 'Active' ? 'success' : lifecycle.status === 'Archived' ? 'warning' : 'danger'
  return <Badge tone={tone}>{t(lifecycle.status === 'Deleted' ? 'Pending deletion' : lifecycle.status)}</Badge>
}

interface Project { id: string; name: string; description: string; createdAt: string; updatedAt?: string; lifecycle: RecordLifecycle }
interface ProjectPage { items: Project[]; page: number; pageSize: number; totalCount: number }
interface OrganizationAccess { permissions: string[] }

export function ProjectsPage() {
  const { t, formatDate } = useModuleI18n()
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
  const [editing, setEditing] = useState<Project | null | undefined>(undefined)
  const [viewing, setViewing] = useState<Project>()
  const [error, setError] = useState<string>()
  const query = useQuery({ queryKey: ['projects', 'active'], queryFn: () => customFetch<ProjectPage>('/api/v1/projects?page=1&pageSize=100&lifecycle=active', { method: 'GET' }) })
  const access = useQuery({ queryKey: ['access'], queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }) })
  const save = useMutation({
    mutationFn: (input: { id?: string; name: string; description: string }) => customFetch<Project>(
      input.id ? `/api/v1/projects/${input.id}` : '/api/v1/projects',
      { method: input.id ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) },
    ),
    onSuccess: async () => { setEditing(undefined); await queryClient.invalidateQueries({ queryKey: ['projects'] }) },
    onError: (reason) => setError(reason instanceof Error ? reason.message : t('Unable to save project')),
  })
  const changeLifecycle = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'archive' | 'restore' }) => customFetch<void>(`/api/v1/projects/${id}/${action}`, { method: 'POST' }),
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
  })

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError(undefined)
    const form = new FormData(event.currentTarget)
    save.mutate({ id: editing?.id, name: String(form.get('name')), description: String(form.get('description') ?? '') })
  }

  const openCreate = () => { setError(undefined); setEditing(null) }
  const actionsFor = (project: Project): RowAction[] => [
    { label: t('View'), icon: 'view', onSelect: () => setViewing(project) },
    { label: t('Edit'), icon: 'edit', onSelect: () => { setError(undefined); setEditing(project) } },
    { label: t('Archive'), icon: 'archive', onSelect: () => changeLifecycle.mutate({ id: project.id, action: 'archive' }) },
  ]
  const columns: DataTableColumn<Project>[] = [
    { id: 'name', header: t('Project'), cell: (project) => <div><strong>{project.name}</strong><small>{project.description}</small></div>, sortValue: (project) => project.name, searchValue: (project) => `${project.name} ${project.description}`, hideable: false },
    { id: 'status', header: t('Status'), cell: (project) => <LifecycleBadge lifecycle={project.lifecycle} />, sortValue: (project) => project.lifecycle.status },
    { id: 'created', header: t('Created'), cell: (project) => formatDate(project.createdAt), sortValue: (project) => new Date(project.createdAt) },
    { id: 'actions', header: '', cell: (project) => <RowActions label={`${t('Actions for')} ${project.name}`} actions={actionsFor(project)} />, hideable: false, align: 'right', width: 54 },
  ]
  return <>
    <PageHeader eyebrow={t('Application')} title={t('Projects')} description={t('Create, manage, archive, and recover organization-scoped projects.')} actions={<Button variant="primary" onClick={openCreate}><Plus size={14} /> {t('New project')}</Button>} />
    <Surface className="collection">
      {query.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div> : query.isError ? <EmptyState title={t('Projects could not be loaded')} description={query.error.message} action={<Button onClick={() => query.refetch()}>{t('Try again')}</Button>} /> : <DataTable labels={dataTableLabels} ariaLabel={t('Projects')} data={query.data?.items ?? []} columns={columns} getRowId={(project) => project.id} searchPlaceholder={t('Search projects…')} initialSort={{ id: 'created', direction: 'desc' }} empty={<EmptyState title={t('No projects')} description={t('Create the first project to exercise organization-scoped RLS.')} action={<Button variant="primary" onClick={openCreate}>{t('Create project')}</Button>} />} />}
    </Surface>
    <ModuleExtensionSlot point="projects.list.after-table" context={{ resultCount: query.data?.items.length ?? 0 }} permissions={access.data?.permissions} />
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && setEditing(undefined)} title={t(editing ? 'Edit project' : 'Create project')} description={t('Changes are authorized in the application layer and isolated by PostgreSQL RLS.')}>
      <form className="dialog-form" onSubmit={submit}>
        <label>{t('Name')}<input name="name" defaultValue={editing?.name} maxLength={120} required autoFocus /></label>
        <label>{t('Description')}<textarea name="description" defaultValue={editing?.description} maxLength={2000} rows={5} /></label>
        {error && <div className="form-error" role="alert">{error}</div>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={() => setEditing(undefined)}>{t('Cancel')}</Button>
          <Button type="submit" variant="primary" disabled={save.isPending}>{t(save.isPending ? 'Saving…' : 'Save project')}</Button>
        </div>
      </form>
    </Dialog>
    <Dialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.name ?? t('Project')} description={t('Project details and record lifecycle.')}>
      {viewing && <dl className="record-details">
        <div><dt>{t('Status')}</dt><dd><LifecycleBadge lifecycle={viewing.lifecycle} /></dd></div>
        <div><dt>{t('Description')}</dt><dd>{viewing.description || t('No description')}</dd></div>
        <div><dt>{t('Created')}</dt><dd>{formatRecordDate(viewing.createdAt)}</dd></div>
        <div><dt>{t('Updated')}</dt><dd>{formatRecordDate(viewing.updatedAt)}</dd></div>
      </dl>}
    </Dialog>
  </>
}
