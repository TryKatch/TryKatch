import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { getWorkspaceCurrentQueryKey } from '@trykatch/api-client'
import { ModuleFeatureProvider, WebModuleCatalog, type FeatureAvailabilityState } from '@trykatch/module-sdk'
import { expect, it } from 'vitest'
import { ModuleRouteBoundary } from './ModuleRouteBoundary'

const catalog = new WebModuleCatalog([{
  id: 'probe', name: 'Probe', version: '1.0.0', description: 'Probe.', requires: [], optionalDependencies: [],
  routes: [], navigation: [], extensionPoints: [], extensions: [],
  features: [{ key: 'probe.reads', name: 'Reads', description: 'Read probe.', defaultEnabled: true }],
}])
const available: FeatureAvailabilityState = { organizationId: 'org-a', status: 'success', snapshot: { organizationId: 'org-a', values: { 'probe.reads': true } } }

it.each([
  ['disabled', { ...available, snapshot: { organizationId: 'org-a', values: { 'probe.reads': false } } }, ['probe.read']],
  ['loading', { ...available, status: 'loading' as const }, ['probe.read']],
  ['outage', { ...available, status: 'error' as const }, ['probe.read']],
  ['permission denial', available, []],
] as const)('does not mount protected route content for %s', (_label, state, permissions) => {
  const client = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity, retry: false } } })
  client.setQueryData(getWorkspaceCurrentQueryKey(), { organizationId: 'org-a', permissions })
  render(<QueryClientProvider client={client}><ModuleFeatureProvider catalog={catalog} state={state}>
    <ModuleRouteBoundary route={{ requiredFeature: 'probe.reads', requiredPermission: 'probe.read' }}><p>Private module content</p></ModuleRouteBoundary>
  </ModuleFeatureProvider></QueryClientProvider>)
  expect(screen.queryByText('Private module content')).not.toBeInTheDocument()
})

it('removes previously mounted content when its feature becomes disabled', () => {
  const client = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity, retry: false } } })
  client.setQueryData(getWorkspaceCurrentQueryKey(), { organizationId: 'org-a', permissions: ['probe.read'] })
  const view = (state: FeatureAvailabilityState) => <QueryClientProvider client={client}><ModuleFeatureProvider catalog={catalog} state={state}>
    <ModuleRouteBoundary route={{ requiredFeature: 'probe.reads', requiredPermission: 'probe.read' }}><p>Private module content</p></ModuleRouteBoundary>
  </ModuleFeatureProvider></QueryClientProvider>
  const { rerender } = render(view(available))
  expect(screen.getByText('Private module content')).toBeInTheDocument()
  rerender(view({ ...available, snapshot: { organizationId: 'org-a', values: { 'probe.reads': false } } }))
  expect(screen.queryByText('Private module content')).not.toBeInTheDocument()
})
