import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from '@tanstack/react-router'
import { customFetch, featuresCurrent, getWorkspaceCurrentQueryKey, workspaceCurrent } from '@trykatch/api-client'
import { ModuleProvider } from '@trykatch/module-sdk'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../i18n/I18nProvider'
import { workspaceModules } from '../modules'
import { ModuleRouteBoundary } from '../features/availability/ModuleRouteBoundary'
import { AppShell } from './AppShell'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  customFetch: vi.fn(),
  featuresCurrent: vi.fn(),
  workspaceCurrent: vi.fn(),
}))

vi.mock('./useWorkspaceRealtimeLifecycle', () => ({ useWorkspaceRealtimeLifecycle: vi.fn() }))
vi.mock('../features/assistant/AssistantChat', () => ({
  AssistantChatProvider: ({ children }: { children: ReactNode }) => children,
  AssistantChat: () => null,
}))

const organizationA = 'organization-a'
const organizationB = 'organization-b'
const permissions = ['projects.read', 'projects.manage']
const projectRoute = workspaceModules.routesFor('workspace').find((route) => route.path === '/projects')!
let accessOrganizationId: string
let workspaceOrganizationId: string
let featureResponseOrganizationId: string
let featureValues: Record<string, boolean>
let accessFailure: (Error & { status?: number }) | undefined
let workspaceFailure: (Error & { status?: number }) | undefined

const accessDto = (organizationId: string) => ({ organizationId, userId: 'user-1', membershipId: `membership-${organizationId}`, permissions })
const workspaceDto = (organizationId: string) => ({ organizationId, membershipId: `membership-${organizationId}`, permissions })

beforeEach(() => {
  accessOrganizationId = organizationA
  workspaceOrganizationId = organizationA
  featureResponseOrganizationId = organizationA
  featureValues = { 'projects.reads': true, 'projects.writes': true }
  accessFailure = undefined
  workspaceFailure = undefined
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.stubGlobal('scrollTo', () => {})
  vi.mocked(customFetch).mockImplementation(async (url) => {
    if (url === '/api/v1/auth/session') return { userId: 'user-1', displayName: 'Test User', email: 'test@example.test', isPlatformAdministrator: false }
    if (url === '/api/v1/access') {
      if (accessFailure) throw accessFailure
      return accessDto(accessOrganizationId)
    }
    return undefined
  })
  vi.mocked(workspaceCurrent).mockImplementation(async () => {
    if (workspaceFailure) throw workspaceFailure
    return workspaceDto(workspaceOrganizationId) as never
  })
  vi.mocked(featuresCurrent).mockImplementation(async () => ({
    organizationId: featureResponseOrganizationId,
    evaluatedAtUtc: '2026-10-24T10:00:00Z',
    values: featureValues,
  }) as never)
})

afterEach(() => {
  cleanup()
  if (vi.isFakeTimers()) {
    vi.clearAllTimers()
    vi.useRealTimers()
  }
  vi.resetAllMocks()
  vi.unstubAllGlobals()
  localStorage.clear()
})

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000, gcTime: Infinity } } })
  const rootRoute = createRootRoute({ component: () => <ModuleProvider catalog={workspaceModules}><Outlet /></ModuleProvider> })
  const shellRoute = createRoute({ getParentRoute: () => rootRoute, id: '_workspace', component: AppShell })
  const projectsRouteForTest = createRoute({
    getParentRoute: () => shellRoute,
    path: '/projects',
    component: () => <ModuleRouteBoundary route={projectRoute}><p>Protected Projects outlet probe</p></ModuleRouteBoundary>,
  })
  const routeTree = rootRoute.addChildren([shellRoute.addChildren([projectsRouteForTest])])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/projects'] }), defaultPendingMinMs: 0 })
  render(<I18nProvider><QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider></I18nProvider>)
  return { client }
}

describe('Projects availability in the actual workspace shell', () => {
  it('fails closed on mismatched access and workspace organizations without requesting features', async () => {
    accessOrganizationId = organizationA
    workspaceOrganizationId = organizationB
    featureResponseOrganizationId = organizationB
    mount()

    expect(await screen.findByRole('link', { name: 'Overview' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
    expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
    expect(featuresCurrent).not.toHaveBeenCalled()
  })

  it('requests the current organization snapshot and exposes the permitted Projects route when enabled', async () => {
    const { client } = mount()

    expect(await screen.findByRole('link', { name: 'Projects' })).toBeInTheDocument()
    expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()
    expect(featuresCurrent).toHaveBeenCalledTimes(1)
    expect(client.getQueryData(['organization-features', organizationA])).toMatchObject({ organizationId: organizationA })
  })

  it('removes the Projects navigation and protected route when the snapshot disables reads', async () => {
    featureValues = { 'projects.reads': false, 'projects.writes': true }
    mount()

    expect(await screen.findByRole('heading', { name: 'Feature unavailable' })).toBeInTheDocument()
    expect(featuresCurrent).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
    expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
  })

  it('drops the prior organization feature decision when access refetch fails after a workspace switch', async () => {
    const { client } = mount()
    expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()

    featureResponseOrganizationId = organizationB
    act(() => {
      client.setQueryData(getWorkspaceCurrentQueryKey(), workspaceDto(organizationB))
      client.setQueryData(['access'], accessDto(organizationB))
    })
    await waitFor(() => expect(client.getQueryData(['organization-features', organizationB])).toMatchObject({ organizationId: organizationB }))
    expect(await screen.findByRole('link', { name: 'Projects' })).toBeInTheDocument()

    accessFailure = new Error('Access unavailable')
    await act(async () => { await client.invalidateQueries({ queryKey: ['access'], exact: true }) })

    expect(client.getQueryState(['access'])?.status).toBe('error')
    expect(client.getQueryData(['organization-features', organizationB])).toMatchObject({ organizationId: organizationB })
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
      expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
    })
  })

  it.each(['access', 'workspace', 'both'] as const)(
    'recovers from a 429 %s query failure only after fresh matching access and workspace snapshots',
    async (failedQuery) => {
      const { client } = mount()
      expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()
      expect(await screen.findByRole('link', { name: 'Projects' })).toBeInTheDocument()
      const initialFeatureRequests = vi.mocked(featuresCurrent).mock.calls.length

      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
      workspaceOrganizationId = organizationB
      accessOrganizationId = organizationB
      featureResponseOrganizationId = organizationB
      if (failedQuery === 'access' || failedQuery === 'both') {
        accessFailure = Object.assign(new Error('Too many requests'), { status: 429 })
      }
      if (failedQuery === 'workspace' || failedQuery === 'both') {
        workspaceFailure = Object.assign(new Error('Too many requests'), { status: 429 })
      }

      await act(async () => {
        await client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true })
      })
      expect(workspaceCurrent).toHaveBeenCalled()

      await act(async () => {
        await client.invalidateQueries({ queryKey: ['access'], exact: true })
      })

      if (failedQuery === 'access' || failedQuery === 'both') {
        expect(client.getQueryState(['access'])?.status).toBe('error')
        expect(client.getQueryState(['access'])?.error).toMatchObject({ status: 429 })
      } else {
        expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB })
      }
      if (failedQuery === 'workspace' || failedQuery === 'both') {
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.status).toBe('error')
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.error).toMatchObject({ status: 429 })
      } else {
        expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB })
      }
      await waitFor(() => {
        expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
        expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
      })
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests)

      accessFailure = undefined
      workspaceFailure = undefined
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000)
      })

      await waitFor(() => {
        expect(screen.getByRole('link', { name: 'Projects' })).toBeInTheDocument()
        expect(screen.getByText('Protected Projects outlet probe')).toBeInTheDocument()
      })
      expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB })
      expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB })
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests + 1)
      expect(client.getQueryData(['organization-features', organizationB])).toMatchObject({ organizationId: organizationB })
    },
  )

  it.each(['access', 'workspace'] as const)(
    'does not automatically retry a 403 %s query failure on the 429 recovery interval',
    async (failedQuery) => {
      const { client } = mount()
      expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()
      const initialFeatureRequests = vi.mocked(featuresCurrent).mock.calls.length

      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
      workspaceOrganizationId = organizationB
      accessOrganizationId = organizationB
      featureResponseOrganizationId = organizationB
      if (failedQuery === 'access') {
        accessFailure = Object.assign(new Error('Forbidden'), { status: 403 })
      } else {
        workspaceFailure = Object.assign(new Error('Forbidden'), { status: 403 })
      }

      await act(async () => {
        await client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true })
      })
      await act(async () => {
        await client.invalidateQueries({ queryKey: ['access'], exact: true })
      })

      const failedAccessRequests = vi.mocked(customFetch).mock.calls.filter(([url]) => url === '/api/v1/access').length
      const failedWorkspaceRequests = vi.mocked(workspaceCurrent).mock.calls.length
      if (failedQuery === 'access') {
        expect(client.getQueryState(['access'])?.status).toBe('error')
        expect(client.getQueryState(['access'])?.error).toMatchObject({ status: 403 })
        expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB })
      } else {
        expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB })
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.status).toBe('error')
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.error).toMatchObject({ status: 403 })
      }
      await waitFor(() => {
        expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
        expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
      })
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests)

      accessFailure = undefined
      workspaceFailure = undefined
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000)
      })

      await waitFor(() => {
        if (failedQuery === 'access') expect(client.getQueryState(['access'])?.status).toBe('error')
        else expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.status).toBe('error')
        expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
        expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
      })
      expect(vi.mocked(customFetch).mock.calls.filter(([url]) => url === '/api/v1/access')).toHaveLength(failedAccessRequests)
      expect(vi.mocked(workspaceCurrent).mock.calls).toHaveLength(failedWorkspaceRequests)
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests)
    },
  )
})
