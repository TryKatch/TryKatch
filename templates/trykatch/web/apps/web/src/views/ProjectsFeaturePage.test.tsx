import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { customFetch, getWorkspaceCurrentQueryKey } from '@trykatch/api-client'
import { ModuleFeatureProvider, ModuleProvider, type FeatureAvailabilityState, type OrganizationFeatureSnapshot } from '@trykatch/module-sdk'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { workspaceModules } from '../modules'
import { ModuleRouteBoundary } from '../features/availability/ModuleRouteBoundary'
import { ProjectsPage } from '../../../../../src/Modules/Projects/Web/ProjectsPage'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(), customFetch: vi.fn(),
}))

const project = {
  id: 'project-1', name: 'Atlas', description: 'Private project description',
  createdAt: '2026-09-01T10:00:00Z', lifecycle: { status: 'Active' as const },
}
let projectPage = { items: [project], page: 1, pageSize: 100, totalCount: 1 }
const organizationId = 'organization-1'
let permissions: string[]
let accessOrganizationId: string
let accessFailure: Error | undefined

beforeEach(() => {
  permissions = ['projects.read', 'projects.manage']
  accessOrganizationId = organizationId
  accessFailure = undefined
  projectPage = { items: [project], page: 1, pageSize: 100, totalCount: 1 }
  vi.mocked(customFetch).mockImplementation(async (url, options) => {
    if (url === '/api/v1/access') {
      if (accessFailure) throw accessFailure
      return { organizationId: accessOrganizationId, membershipId: 'membership-1', permissions }
    }
    if (url.startsWith('/api/v1/projects') && options?.method === 'GET') return projectPage
    return undefined
  })
})

afterEach(() => { cleanup(); vi.resetAllMocks() })

function availability(reads: boolean, writes: boolean, status: FeatureAvailabilityState['status'] = 'success',
  currentOrganization = organizationId, snapshotOrganization = currentOrganization): FeatureAvailabilityState {
  const snapshot: OrganizationFeatureSnapshot = {
    organizationId: snapshotOrganization,
    values: { 'projects.reads': reads, 'projects.writes': writes },
  }
  return { organizationId: currentOrganization, snapshot, status }
}

function mount(state: FeatureAvailabilityState) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const view = (featureState: FeatureAvailabilityState) => <QueryClientProvider client={client}>
    <ModuleProvider catalog={workspaceModules}>
      <ModuleFeatureProvider catalog={workspaceModules} state={featureState}>
        <ProjectsPage />
      </ModuleFeatureProvider>
    </ModuleProvider>
  </QueryClientProvider>
  const rendered = render(view(state))
  return { ...rendered, client, renderState: (next: FeatureAvailabilityState) => rendered.rerender(view(next)) }
}

const projectGets = () => vi.mocked(customFetch).mock.calls.filter(([url, options]) =>
  url.startsWith('/api/v1/projects') && options?.method === 'GET')

const projectsRoute = workspaceModules.routesFor('workspace').find((route) => route.id === 'projects.list')!

function mountThroughRouteBoundary(state: FeatureAvailabilityState) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryDefaults(getWorkspaceCurrentQueryKey(), { staleTime: 30_000 })
  const seedWorkspace = (featureState: FeatureAvailabilityState) => {
    if (featureState.organizationId) {
      client.setQueryData(getWorkspaceCurrentQueryKey(), {
        organizationId: featureState.organizationId,
        permissions,
      })
    }
  }
  const view = (featureState: FeatureAvailabilityState) => <QueryClientProvider client={client}>
    <ModuleProvider catalog={workspaceModules}>
      <ModuleFeatureProvider catalog={workspaceModules} state={featureState}>
        <ModuleRouteBoundary route={projectsRoute}>
          <ProjectsPage />
        </ModuleRouteBoundary>
      </ModuleFeatureProvider>
    </ModuleProvider>
  </QueryClientProvider>
  seedWorkspace(state)
  const rendered = render(view(state))
  return {
    ...rendered,
    client,
    renderState: (next: FeatureAvailabilityState) => {
      seedWorkspace(next)
      rendered.rerender(view(next))
    },
  }
}

describe('Projects feature availability', () => {
  for (const [label, state] of [
    ['disabled', availability(false, true)],
    ['loading', availability(true, true, 'loading')],
    ['error', availability(true, true, 'error')],
    ['stale organization snapshot', availability(true, true, 'success', organizationId, 'organization-2')],
  ] as const) {
    it(`fails closed for ${label} reads without querying or showing protected project data`, async () => {
      mount(state)

      await screen.findByRole('heading', { name: 'Projects' })
      expect(projectGets()).toHaveLength(0)
      expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
      expect(screen.queryByText('Private project description')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
    })
  }

  it('keeps reads and View available when writes are disabled, while hiding write actions', async () => {
    mount(availability(true, false))

    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    expect(projectGets()).toHaveLength(1)
    expect(screen.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    expect(screen.getByRole('menuitem', { name: 'View' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: 'Archive' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('menuitem', { name: 'View' }))
    expect(within(await screen.findByRole('dialog')).getByText('Private project description')).toBeInTheDocument()
  })

  it('does not let enabled features grant missing project-management permission', async () => {
    permissions = ['projects.read']
    mount(availability(true, true))

    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    expect(screen.getByRole('menuitem', { name: 'View' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: 'Archive' })).not.toBeInTheDocument()
  })

  it('does not use access permissions returned for a previous organization', async () => {
    accessOrganizationId = 'organization-1'
    mount(availability(true, true, 'success', 'organization-2'))

    expect(await screen.findByRole('heading', { name: 'Project access unavailable' })).toBeInTheDocument()
    expect(projectGets()).toHaveLength(0)
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
  })

  it('does not keep previously successful permissions after a current access refetch fails', async () => {
    const mounted = mount(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()

    accessFailure = new Error('Access unavailable')
    await act(async () => {
      await mounted.client.invalidateQueries({ queryKey: ['access', organizationId] })
    })

    expect(await screen.findByRole('heading', { name: 'Project access unavailable' })).toBeInTheDocument()
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(projectGets()).toHaveLength(1)
  })

  it('discards a prior organization view when changing organizations', async () => {
    const mounted = mount(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'View' }))
    expect(within(await screen.findByRole('dialog')).getByText('Private project description')).toBeInTheDocument()

    const organizationTwoProject = { ...project, id: 'project-2', name: 'Beacon', description: 'Organization two project' }
    accessOrganizationId = 'organization-2'
    projectPage = { items: [organizationTwoProject], page: 1, pageSize: 100, totalCount: 1 }
    mounted.renderState(availability(true, true, 'success', 'organization-2'))

    expect(await screen.findByText('Beacon')).toBeInTheDocument()
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(screen.queryByText('Private project description')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects/project-1' && options?.method !== 'GET')).toBe(false)
  })

  it('rechecks current writes availability before submitting an already-open form', async () => {
    const mounted = mount(availability(true, true))
    fireEvent.click(await screen.findByRole('button', { name: 'New project' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create project' })
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), { target: { value: 'Later project' } })
    const form = dialog.querySelector('form')!

    mounted.renderState(availability(true, false))
    await act(async () => { fireEvent.submit(form) })
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects' && options?.method === 'POST')).toBe(false)
  })

  it('hides cached rows and details when reads become disabled', async () => {
    const mounted = mount(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'View' }))
    expect(within(await screen.findByRole('dialog')).getByText('Private project description')).toBeInTheDocument()

    mounted.renderState(availability(false, true))

    await waitFor(() => {
      expect(screen.queryAllByText('Atlas')).toHaveLength(0)
      expect(screen.queryAllByText('Private project description')).toHaveLength(0)
    })
    expect(projectGets()).toHaveLength(1)
  })

  it('restores a typed create draft after a successful feature refresh remounts the route', async () => {
    const mounted = mountThroughRouteBoundary(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'New project' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create project' })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Name' }), { target: { value: 'Draft project' } })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Draft description' } })
    const savedForm = dialog.querySelector('form')!

    mounted.renderState(availability(true, true, 'loading'))
    expect(screen.getByRole('status', { name: 'Checking feature availability' })).toBeInTheDocument()
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Create project' })).not.toBeInTheDocument()
    await act(async () => { fireEvent.submit(savedForm) })
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects' && options?.method === 'POST')).toBe(false)

    mounted.renderState(availability(true, true))
    const restoredDialog = await screen.findByRole('dialog', { name: 'Create project' })
    expect(within(restoredDialog).getByRole('textbox', { name: 'Name' })).toHaveValue('Draft project')
    expect(within(restoredDialog).getByRole('textbox', { name: 'Description' })).toHaveValue('Draft description')
  })

  it('restores a typed edit draft after a successful feature refresh remounts the route', async () => {
    const mounted = mountThroughRouteBoundary(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit project' })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Name' }), { target: { value: 'Draft Atlas' } })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Edited draft description' } })
    const savedForm = dialog.querySelector('form')!

    mounted.renderState(availability(true, true, 'loading'))
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Edit project' })).not.toBeInTheDocument()
    await act(async () => { fireEvent.submit(savedForm) })
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects/project-1' && options?.method === 'PUT')).toBe(false)

    mounted.renderState(availability(true, true))
    const restoredDialog = await screen.findByRole('dialog', { name: 'Edit project' })
    expect(within(restoredDialog).getByRole('textbox', { name: 'Name' })).toHaveValue('Draft Atlas')
    expect(within(restoredDialog).getByRole('textbox', { name: 'Description' })).toHaveValue('Edited draft description')
  })

  it.each([
    ['writes become disabled', (state: FeatureAvailabilityState) => availability(true, false, state.status)],
    ['management permission is removed', (state: FeatureAvailabilityState) => {
      permissions = ['projects.read']
      return availability(true, true, state.status)
    }],
  ] as const)('clears a create draft after %s', async (_label, deniedState) => {
    permissions = ['projects.read', 'projects.manage']
    const mounted = mountThroughRouteBoundary(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'New project' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create project' })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Name' }), { target: { value: 'Denied draft' } })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Must be discarded' } })
    const savedForm = dialog.querySelector('form')!

    mounted.renderState(deniedState(availability(true, true)))
    await act(async () => {
      await mounted.client.invalidateQueries({ queryKey: ['access', organizationId], exact: true })
    })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Create project' })).not.toBeInTheDocument())
    await act(async () => { fireEvent.submit(savedForm) })
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects' && options?.method === 'POST')).toBe(false)

    permissions = ['projects.read', 'projects.manage']
    mounted.renderState(availability(true, true))
    await act(async () => {
      await mounted.client.invalidateQueries({ queryKey: ['access', organizationId], exact: true })
    })
    expect(screen.queryByRole('dialog', { name: 'Create project' })).not.toBeInTheDocument()
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects' && options?.method === 'POST')).toBe(false)
  })

  it('does not show an organization-one create draft after switching to organization two', async () => {
    const mounted = mountThroughRouteBoundary(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'New project' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create project' })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Name' }), { target: { value: 'Organization one draft' } })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Private to organization one' } })

    accessOrganizationId = 'organization-2'
    projectPage = { items: [{ ...project, id: 'project-2', name: 'Beacon', description: 'Organization two project' }], page: 1, pageSize: 100, totalCount: 1 }
    mounted.renderState(availability(true, true, 'success', 'organization-2'))

    expect(await screen.findByText('Beacon')).toBeInTheDocument()
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Create project' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'New project' }))
    const organizationTwoDialog = await screen.findByRole('dialog', { name: 'Create project' })
    expect(within(organizationTwoDialog).getByRole('textbox', { name: 'Name' })).toHaveValue('')
    expect(within(organizationTwoDialog).getByRole('textbox', { name: 'Description' })).toHaveValue('')
  })
})
