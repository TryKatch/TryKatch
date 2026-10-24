import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { customFetch, featuresCurrent, type OrganizationFeatureSnapshot } from '@trykatch/api-client'
import { isModuleFeatureEnabled, WebModuleCatalog } from '@trykatch/module-sdk'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { organizationFeatureQueryKey, useOrganizationFeatures } from './useOrganizationFeatures'

vi.mock('@trykatch/api-client', async original => ({
  ...await original<typeof import('@trykatch/api-client')>(), featuresCurrent: vi.fn(),
}))

const catalog = new WebModuleCatalog([{
  id: 'probe', name: 'Probe', version: '1.0.0', description: 'Feature probe.', requires: [], optionalDependencies: [],
  routes: [], navigation: [], extensionPoints: [], extensions: [],
  features: [{ key: 'probe.reads', name: 'Reads', description: 'Read probe data.', defaultEnabled: true }],
}])

function snapshot(organizationId: string, enabled = true): OrganizationFeatureSnapshot {
  return { organizationId, evaluatedAtUtc: '2026-10-24T10:00:00Z', values: { 'probe.reads': enabled } }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function Probe({ organizationId }: { organizationId?: string }) {
  const state = useOrganizationFeatures(organizationId)
  return <output>{`${state.organizationId ?? 'none'}:${state.status}:${isModuleFeatureEnabled(catalog, state, 'probe.reads')}`}</output>
}

const clients: QueryClient[] = []
function mount(organizationId?: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  clients.push(client)
  const view = (id?: string) => <QueryClientProvider client={client}><Probe organizationId={id} /></QueryClientProvider>
  const rendered = render(view(organizationId))
  return { client, ...rendered, switchTo: (id?: string) => rendered.rerender(view(id)) }
}

beforeEach(() => { vi.mocked(featuresCurrent).mockReset() })
afterEach(async () => {
  cleanup()
  for (const client of clients.splice(0)) { await client.cancelQueries(); client.clear() }
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

it('does not request or enable features without a resolved organization', async () => {
  mount()
  expect(screen.getByText('none:loading:false')).toBeInTheDocument()
  expect(featuresCurrent).not.toHaveBeenCalled()
})

it('cancels the previous workspace request and cannot accept its late response', async () => {
  const first = deferred<OrganizationFeatureSnapshot>()
  const second = deferred<OrganizationFeatureSnapshot>()
  let firstSignal: AbortSignal | undefined
  vi.mocked(featuresCurrent).mockImplementationOnce(options => { firstSignal = options?.signal ?? undefined; return first.promise })
    .mockImplementationOnce(() => second.promise)
  const mounted = mount('org-a')
  await waitFor(() => expect(featuresCurrent).toHaveBeenCalledTimes(1))
  mounted.switchTo('org-b')
  expect(screen.getByText('org-b:loading:false')).toBeInTheDocument()
  await waitFor(() => expect(featuresCurrent).toHaveBeenCalledTimes(2))
  expect(firstSignal?.aborted).toBe(true)
  await act(async () => { second.resolve(snapshot('org-b', false)); first.resolve(snapshot('org-a')) })
  expect(await screen.findByText('org-b:success:false')).toBeInTheDocument()
  expect(mounted.client.getQueryData(organizationFeatureQueryKey('org-b'))).toMatchObject({ organizationId: 'org-b' })
})

it('hides a previously enabled snapshot during refresh and after failure, then recovers on focus', async () => {
  const refresh = deferred<OrganizationFeatureSnapshot>()
  vi.mocked(featuresCurrent).mockResolvedValueOnce(snapshot('org-a')).mockImplementationOnce(() => refresh.promise)
    .mockResolvedValueOnce(snapshot('org-a'))
  const { client } = mount('org-a')
  expect(await screen.findByText('org-a:success:true')).toBeInTheDocument()
  await act(async () => { void client.invalidateQueries({ queryKey: organizationFeatureQueryKey('org-a') }) })
  expect(await screen.findByText('org-a:loading:false')).toBeInTheDocument()
  await act(async () => { refresh.reject(new Error('Policy store unavailable')) })
  expect(await screen.findByText('org-a:error:false')).toBeInTheDocument()
  expect(featuresCurrent).toHaveBeenCalledTimes(2)
  act(() => window.dispatchEvent(new Event('focus')))
  expect(await screen.findByText('org-a:success:true')).toBeInTheDocument()
  expect(featuresCurrent).toHaveBeenCalledTimes(3)
})

it('refreshes availability after an actual feature rejection without keeping the enabled snapshot', async () => {
  const refresh = deferred<OrganizationFeatureSnapshot>()
  vi.mocked(featuresCurrent).mockResolvedValueOnce(snapshot('org-a')).mockImplementationOnce(() => refresh.promise)
  mount('org-a')
  expect(await screen.findByText('org-a:success:true')).toBeInTheDocument()
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: 'feature_unavailable' }), {
    status: 409, headers: { 'Content-Type': 'application/problem+json' },
  })))
  await act(async () => { await expect(customFetch('/api/v1/probe', { method: 'GET' })).rejects.toMatchObject({ status: 409 }) })
  expect(await screen.findByText('org-a:loading:false')).toBeInTheDocument()
  await waitFor(() => expect(featuresCurrent).toHaveBeenCalledTimes(2))
  await act(async () => { refresh.resolve(snapshot('org-a', false)) })
  expect(await screen.findByText('org-a:success:false')).toBeInTheDocument()
})

it('polls the current organization every thirty seconds', async () => {
  vi.useFakeTimers()
  vi.mocked(featuresCurrent).mockResolvedValue(snapshot('org-a'))
  mount('org-a')
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(featuresCurrent).toHaveBeenCalledTimes(1)
  await act(async () => { await vi.advanceTimersByTimeAsync(30_000) })
  expect(featuresCurrent).toHaveBeenCalledTimes(2)
})
