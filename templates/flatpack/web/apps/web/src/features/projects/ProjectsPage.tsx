import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch } from '@flatpackapp/api-client'
import { Button, DataTable, Dialog, EmptyState, PageHeader, RowActions, Skeleton, Surface, type DataTableColumn, type RowAction } from '@flatpackapp/ui'
import { Plus } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { formatRecordDate, LifecycleBadge, RecordDetailsDialog, type RecordLifecycle } from '../../components/RecordLifecycle'
import { ModuleExtensionSlot } from '../../module-system/ModuleExtensionSlot'

interface Project { id: string; name: string; description: string; createdAt: string; updatedAt?: string; lifecycle: RecordLifecycle }
interface ProjectPage { items: Project[]; page: number; pageSize: number; totalCount: number }
interface OrganizationAccess { permissions: string[] }

export function ProjectsPage() {
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
    onError: (reason) => setError(reason instanceof Error ? reason.message : 'Unable to save project'),
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
    { label: 'View', icon: 'view', onSelect: () => setViewing(project) },
    { label: 'Edit', icon: 'edit', onSelect: () => { setError(undefined); setEditing(project) } },
    { label: 'Archive', icon: 'archive', onSelect: () => changeLifecycle.mutate({ id: project.id, action: 'archive' }) },
  ]
  const columns: DataTableColumn<Project>[] = [
    { id: 'name', header: 'Project', cell: (project) => <div><strong>{project.name}</strong><small>{project.description}</small></div>, sortValue: (project) => project.name, searchValue: (project) => `${project.name} ${project.description}`, hideable: false },
    { id: 'status', header: 'Status', cell: (project) => <LifecycleBadge lifecycle={project.lifecycle} />, sortValue: (project) => project.lifecycle.status },
    { id: 'created', header: 'Created', cell: (project) => new Date(project.createdAt).toLocaleDateString(), sortValue: (project) => new Date(project.createdAt) },
    { id: 'actions', header: '', cell: (project) => <RowActions label={`Actions for ${project.name}`} actions={actionsFor(project)} />, hideable: false, align: 'right', width: 54 },
  ]
  return <>
    <PageHeader eyebrow="Application" title="Projects" description="Create, manage, archive, and recover organization-scoped projects." actions={<Button variant="primary" onClick={openCreate}><Plus size={14} /> New project</Button>} />
    <Surface className="collection">
      {query.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div> : query.isError ? <EmptyState title="Projects could not be loaded" description={query.error.message} action={<Button onClick={() => query.refetch()}>Try again</Button>} /> : <DataTable ariaLabel="Projects" data={query.data?.items ?? []} columns={columns} getRowId={(project) => project.id} searchPlaceholder="Search projects…" initialSort={{ id: 'created', direction: 'desc' }} empty={<EmptyState title="No projects" description="Create the first project to exercise organization-scoped RLS." action={<Button variant="primary" onClick={openCreate}>Create project</Button>} />} />}
      <ModuleExtensionSlot point="projects.list.after-table" context={{ resultCount: query.data?.items.length ?? 0 }} permissions={access.data?.permissions} />
    </Surface>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && setEditing(undefined)} title={editing ? 'Edit project' : 'Create project'} description="Changes are authorized in the application layer and isolated by PostgreSQL RLS.">
      <form className="dialog-form" onSubmit={submit}>
        <label>Name<input name="name" defaultValue={editing?.name} maxLength={120} required autoFocus /></label>
        <label>Description<textarea name="description" defaultValue={editing?.description} maxLength={2000} rows={5} /></label>
        {error && <div className="form-error" role="alert">{error}</div>}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={() => setEditing(undefined)}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save project'}</Button>
        </div>
      </form>
    </Dialog>
    <RecordDetailsDialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.name ?? 'Project'} description="Project details and record lifecycle." recordType="Project" status={viewing ? <LifecycleBadge lifecycle={viewing.lifecycle} /> : undefined} details={viewing ? [
      { label: 'Name', value: viewing.name }, { label: 'Status', value: <LifecycleBadge lifecycle={viewing.lifecycle} /> },
      { label: 'Description', value: viewing.description || 'No description' }, { label: 'Created', value: formatRecordDate(viewing.createdAt) },
      { label: 'Updated', value: formatRecordDate(viewing.updatedAt) }, { label: 'Archived', value: formatRecordDate(viewing.lifecycle.archivedAt) },
      ...(viewing.lifecycle.deletionReason ? [{ label: 'Deletion reason', value: viewing.lifecycle.deletionReason }] : []),
    ] : []} />
  </>
}
