import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { customFetch, getWorkspaceCurrentQueryKey, workspaceCurrent } from '@trykatch/api-client'
import { ModuleFeatureProvider, type FeatureAvailabilityState } from '@trykatch/module-sdk'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { workspaceModules } from '../../modules'
import { I18nProvider } from '../../i18n/I18nProvider'
import { ArchivePage } from './ArchivePage'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  customFetch: vi.fn(),
  workspaceCurrent: vi.fn(),
}))

const organizationId = 'organization-1'
const project = {
  id: 'project-1', name: 'Atlas', description: 'Private archived project',
  lifecycle: { status: 'Archived' as const, archivedAt: '2026-09-07T10:00:00Z' },
}
let permissions: string[]
let nextAccessResponse: Promise<{ organizationId: string; membershipId: string; permissions: string[] }> | undefined
let nextWorkspaceResponse: Promise<{ organizationId: string; membershipId: string; permissions: string[] }> | undefined
const workspacePermissions = ['projects.read', 'projects.manage']
const workspaceResponse = (organizationId: string) => ({ organizationId, membershipId: `member-${organizationId}`, permissions: [...workspacePermissions] })

function availability(reads: boolean, writes: boolean, currentOrganizationId = organizationId, snapshotOrganizationId = currentOrganizationId): FeatureAvailabilityState {
  return {
    organizationId: currentOrganizationId,
    status: 'success',
    snapshot: { organizationId: snapshotOrganizationId, values: { 'projects.reads': reads, 'projects.writes': writes } },
  }
}

function mount(state: FeatureAvailabilityState) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const view = (featureState: FeatureAvailabilityState) => <I18nProvider><QueryClientProvider client={client}>
    <ModuleFeatureProvider catalog={workspaceModules} state={featureState}><ArchivePage /></ModuleFeatureProvider>
  </QueryClientProvider></I18nProvider>
  const rendered = render(view(state))
  return { ...rendered, client, renderState: (next: FeatureAvailabilityState) => rendered.rerender(view(next)) }
}

function projectGets() {
  return vi.mocked(customFetch).mock.calls.filter(([url, options]) =>
    typeof url === 'string' && url.startsWith('/api/v1/projects?') && options?.method === 'GET')
}

beforeEach(() => {
  permissions = ['projects.read', 'projects.manage']
  nextAccessResponse = undefined
  nextWorkspaceResponse = undefined
  vi.mocked(customFetch).mockImplementation(async (url) => {
    if (url === '/api/v1/access') {
      if (nextAccessResponse) {
        const pending = nextAccessResponse
        nextAccessResponse = undefined
        return pending
      }
      return { organizationId, membershipId: 'member-1', permissions }
    }
    if (typeof url === 'string' && url.startsWith('/api/v1/projects?')) {
      return { items: [project], page: 1, pageSize: 100, totalCount: 1 }
    }
    return undefined
  })
  vi.mocked(workspaceCurrent).mockImplementation(async () => {
    if (nextWorkspaceResponse) {
      const pending = nextWorkspaceResponse
      nextWorkspaceResponse = undefined
      return pending as never
    }
    return workspaceResponse(organizationId) as never
  })
})

afterEach(() => { vi.resetAllMocks() })

describe('Archive project feature availability', () => {
  it.each([
    ['reads disabled', availability(false, true)],
    ['feature snapshot missing for the current organization', availability(true, true, organizationId, 'organization-2')],
    ['reads availability missing', { organizationId, status: 'success', snapshot: { organizationId, values: {} } } as FeatureAvailabilityState],
  ] as const)('does not load or expose project archive data when %s', async (_label, state) => {
    let resolveAccess!: (value: { organizationId: string; membershipId: string; permissions: string[] }) => void
    const accessResult = new Promise<{ organizationId: string; membershipId: string; permissions: string[] }>((resolve) => { resolveAccess = resolve })
    vi.mocked(customFetch).mockImplementation(async (url) => {
      if (url === '/api/v1/access') return accessResult
      if (typeof url === 'string' && url.startsWith('/api/v1/projects?')) {
        return { items: [project], page: 1, pageSize: 100, totalCount: 1 }
      }
      return undefined
    })
    mount(state)

    await screen.findByRole('heading', { name: 'Archive' })
    await waitFor(() => expect(customFetch).toHaveBeenCalledWith('/api/v1/access', expect.objectContaining({ method: 'GET' })))
    await act(async () => { resolveAccess({ organizationId, membershipId: 'member-1', permissions }) })
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
    expect(projectGets()).toHaveLength(0)
    const summary = screen.getByLabelText('Archive summary')
    expect([...summary.querySelectorAll('strong')].map((node) => node.textContent)).toEqual(['0', '0', '0'])
  })

  it('does not load projects when the access response belongs to a different organization', async () => {
    let resolveAccess!: (value: { organizationId: string; membershipId: string; permissions: string[] }) => void
    const accessResult = new Promise<{ organizationId: string; membershipId: string; permissions: string[] }>((resolve) => { resolveAccess = resolve })
    vi.mocked(customFetch).mockImplementation(async (url) => {
      if (url === '/api/v1/access') return accessResult
      if (typeof url === 'string' && url.startsWith('/api/v1/projects?')) return { items: [project], page: 1, pageSize: 100, totalCount: 1 }
      return undefined
    })
    mount(availability(true, true))

    await waitFor(() => expect(customFetch).toHaveBeenCalledWith('/api/v1/access', expect.objectContaining({ method: 'GET' })))
    await act(async () => { resolveAccess({ organizationId: 'organization-2', membershipId: 'member-2', permissions }) })
    expect(projectGets()).toHaveLength(0)
    expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
  })

  it('removes cached rows, counts, details and deletion confirmation when reads are disabled', async () => {
    const mounted = mount(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    expect(projectGets()).toHaveLength(1)
    const summary = screen.getByLabelText('Archive summary')
    expect([...summary.querySelectorAll('strong')].map((node) => node.textContent)).toEqual(['1', '1', '0'])

    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'View' }))
    expect(within(await screen.findByRole('dialog')).getByText('project-1')).toBeInTheDocument()
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()

    mounted.renderState(availability(false, true))
    await waitFor(() => {
      expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
      expect(screen.queryByText('project-1')).not.toBeInTheDocument()
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect([...summary.querySelectorAll('strong')].map((node) => node.textContent)).toEqual(['0', '0', '0'])
    })
    expect(projectGets()).toHaveLength(1)

    mounted.renderState(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('does not submit an already-open deletion confirmation after writes are disabled', async () => {
    const mounted = mount(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }))
    fireEvent.change(await screen.findByRole('textbox', { name: 'Reason for deletion' }), { target: { value: 'No longer required by this team' } })
    const staleSubmit = screen.getByRole('button', { name: 'Request deletion' })

    mounted.renderState(availability(true, false))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await act(async () => {
      fireEvent.click(staleSubmit)
      await Promise.resolve()
    })

    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects/project-1' && options?.method === 'DELETE')).toBe(false)

    mounted.renderState(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('preserves the delete reason through a pending same-organization feature check but clears it after a confirmed denial', async () => {
    const mounted = mount(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }))
    const reason = 'No longer required by this team'
    fireEvent.change(await screen.findByRole('textbox', { name: 'Reason for deletion' }), { target: { value: reason } })
    const staleSubmit = screen.getByRole('button', { name: 'Request deletion' })

    mounted.renderState({ organizationId, status: 'loading', snapshot: availability(true, true).snapshot })
    await waitFor(() => {
      expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    fireEvent.click(staleSubmit)
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects/project-1' && options?.method === 'DELETE')).toBe(false)
    expect(projectGets()).toHaveLength(1)

    mounted.renderState(availability(true, true))
    const resumedDialog = await screen.findByRole('dialog')
    expect(within(resumedDialog).getByText('Atlas')).toBeInTheDocument()
    expect(within(resumedDialog).getByRole('textbox', { name: 'Reason for deletion' })).toHaveValue(reason)

    mounted.renderState(availability(true, false))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    mounted.renderState(availability(true, true))
    expect(await screen.findByText('Atlas')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === '/api/v1/projects/project-1' && options?.method === 'DELETE')).toBe(false)
  })

  it.each(['access', 'workspace'] as const)(
    'hides Archive data and blocks a stale deletion during a pending same-organization %s refresh, then restores the reason',
    async (pendingQuery) => {
      const mounted = mount(availability(true, true))
      expect(await screen.findByText('Atlas')).toBeInTheDocument()
      fireEvent.click(await screen.findByRole('button', { name: 'Actions for Atlas' }))
      fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }))
      const reason = 'No longer required by this team'
      fireEvent.change(await screen.findByRole('textbox', { name: 'Reason for deletion' }), { target: { value: reason } })
      const staleSubmit = screen.getByRole('button', { name: 'Request deletion' })
      let resolveAccess!: (value: { organizationId: string; membershipId: string; permissions: string[] }) => void
      let resolveWorkspace!: (value: { organizationId: string; membershipId: string; permissions: string[] }) => void
      if (pendingQuery === 'access') {
        nextAccessResponse = new Promise((resolve) => { resolveAccess = resolve })
      } else {
        nextWorkspaceResponse = new Promise((resolve) => { resolveWorkspace = resolve })
      }

      let refreshPromise!: Promise<void>
      act(() => {
        refreshPromise = mounted.client.invalidateQueries({
          queryKey: pendingQuery === 'access' ? ['access'] : getWorkspaceCurrentQueryKey(),
          exact: true,
        })
      })
      await waitFor(() => {
        expect(screen.queryByText('Atlas')).not.toBeInTheDocument()
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      })
      fireEvent.click(staleSubmit)
      expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
        url === '/api/v1/projects/project-1' && options?.method === 'DELETE')).toBe(false)
      expect(projectGets()).toHaveLength(1)

      await act(async () => {
        if (pendingQuery === 'access') resolveAccess({ organizationId, membershipId: 'member-1', permissions })
        else resolveWorkspace(workspaceResponse(organizationId))
        await refreshPromise
      })
      const resumed = await screen.findByRole('dialog')
      expect(within(resumed).getByRole('textbox', { name: 'Reason for deletion' })).toHaveValue(reason)
      expect(within(resumed).getByText('Atlas')).toBeInTheDocument()
      expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
        url === '/api/v1/projects/project-1' && options?.method === 'DELETE')).toBe(false)
    },
  )
})
