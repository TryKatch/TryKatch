import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { userEvent } from 'storybook/test'
import { customFetch, organizationsList, platformFeaturesHistory, platformFeaturesList, platformFeaturesSaveEnvironment, platformFeaturesSaveOrganization, type FeatureAdministrationItem, type FeatureControlState } from '@trykatch/api-client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { PlatformFeaturesPage } from './PlatformFeaturesPage'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  customFetch: vi.fn(),
  organizationsList: vi.fn(),
  platformFeaturesHistory: vi.fn(),
  platformFeaturesList: vi.fn(),
  platformFeaturesSaveEnvironment: vi.fn(),
  platformFeaturesSaveOrganization: vi.fn(),
}))

const feature: FeatureAdministrationItem = {
  moduleId: 'projects',
  declaration: { key: 'projects.reads', name: 'Project reads', description: 'View projects and their details.', defaultEnabled: true },
  deploymentDisabled: false,
  configuredDefault: null,
  environmentControl: { target: { featureKey: 'projects.reads' }, enabled: null, forceDisabled: false, version: 'environment-v1', updatedAtUtc: null },
  organizationControl: { target: { featureKey: 'projects.reads', organizationId: 'organization-1' }, enabled: true, forceDisabled: false, version: 'organization-v1', updatedAtUtc: null },
}
const history = {
  items: [
    { id: 'history-1', target: { featureKey: 'projects.reads' }, actorId: 'actor-1', previousVersion: null, version: 'environment-v1', previousEnabled: null, enabled: false, previousForceDisabled: null, forceDisabled: false, changedAtUtc: '2026-10-24T10:00:00Z' },
    { id: 'history-2', target: { featureKey: 'projects.reads' }, actorId: 'actor-2', previousVersion: 'environment-v1', version: 'environment-v2', previousEnabled: false, enabled: true, previousForceDisabled: false, forceDisabled: false, changedAtUtc: '2026-10-24T09:00:00Z' },
  ], page: 1, pageSize: 25, totalCount: 26,
}
let permissions: string[]
let snapshot: FeatureAdministrationItem[]
const originalPointerCapture = Element.prototype.hasPointerCapture
const originalSetPointerCapture = Element.prototype.setPointerCapture
const originalReleasePointerCapture = Element.prototype.releasePointerCapture
const originalScrollIntoView = Element.prototype.scrollIntoView

beforeEach(() => {
  Element.prototype.hasPointerCapture = vi.fn(() => false)
  Element.prototype.setPointerCapture = vi.fn()
  Element.prototype.releasePointerCapture = vi.fn()
  Element.prototype.scrollIntoView = vi.fn()
  permissions = ['platform.features.read', 'platform.features.manage']
  snapshot = [feature]
  vi.mocked(customFetch).mockImplementation(async (url) => {
    if (url === '/api/v1/auth/session') return { displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, platformPermissions: permissions } as never
    return undefined as never
  })
  vi.mocked(platformFeaturesList).mockImplementation(async (params) => ({
    environment: 'development', organizationId: params?.organizationId ?? null,
    items: params?.organizationId ? snapshot : snapshot.map((item) => ({ ...item, organizationControl: null })),
  }) as never)
  vi.mocked(platformFeaturesHistory).mockImplementation(async (_key, params) => ({
    items: [history.items[(Number(params?.page ?? 1) - 1) % history.items.length]],
    page: params?.page ?? 1, pageSize: 25, totalCount: 26,
  }) as never)
  vi.mocked(platformFeaturesSaveEnvironment).mockImplementation(async (_key, body) => ({ ...feature.environmentControl, enabled: body.defaultEnabled, forceDisabled: body.forceDisabled, version: 'environment-v2' }) as never)
  vi.mocked(platformFeaturesSaveOrganization).mockImplementation(async (_key, organizationId, body) => ({ ...feature.organizationControl, target: { featureKey: 'projects.reads', organizationId }, enabled: body.enabled, version: 'organization-v2' }) as never)
  vi.mocked(organizationsList).mockResolvedValue({ items: [{ id: 'organization-1', name: 'Northwind', slug: 'northwind', isActive: true, createdAt: '2026-01-01T00:00:00Z' }], page: 1, pageSize: 25, totalCount: 1 })
})

afterEach(() => {
  cleanup(); vi.clearAllMocks()
  Element.prototype.hasPointerCapture = originalPointerCapture
  Element.prototype.setPointerCapture = originalSetPointerCapture
  Element.prototype.releasePointerCapture = originalReleasePointerCapture
  Element.prototype.scrollIntoView = originalScrollIntoView
})

function mount(organizationId?: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><PlatformFeaturesPage organizationId={organizationId} /></QueryClientProvider>)
  return client
}

it('reads linked feature controls without using the tenant directory permission', async () => {
  permissions = ['platform.features.read']
  mount('organization-1')
  expect(await screen.findByText('Project reads')).toBeInTheDocument()
  expect(platformFeaturesList).toHaveBeenCalledWith({ organizationId: 'organization-1' }, expect.anything())
  expect(organizationsList).not.toHaveBeenCalled()
  expect(screen.getByText('Read-only access')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Save/ })).not.toBeInTheDocument()
})

it('loads a paged tenant selector only with tenant-read permission', async () => {
  permissions = ['platform.features.read', 'platform.tenants.read']
  mount()
  expect(await screen.findByRole('combobox', { name: 'Organization override target' })).toBeInTheDocument()
  await waitFor(() => expect(organizationsList).toHaveBeenCalledWith({ page: 1, pageSize: 25 }, expect.anything()))
})

it('keeps a linked organization target visible when it is absent from the current directory page', async () => {
  permissions = ['platform.features.read', 'platform.tenants.read']
  mount('organization-2')
  expect(await screen.findByText('Project reads')).toBeInTheDocument()
  expect(screen.getByRole('combobox', { name: 'Organization override target' })).toHaveTextContent('Linked organization (organization-2)')
  expect(platformFeaturesList).toHaveBeenCalledWith({ organizationId: 'organization-2' }, expect.anything())
})

it('saves environment and organization controls using their displayed versions and supports inheritance reset', async () => {
  const user = userEvent.setup()
  mount('organization-1')
  await screen.findByText('Project reads')

  await user.click(screen.getByRole('combobox', { name: 'Environment control for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  await user.click(screen.getByRole('button', { name: 'Save environment control for Project reads' }))
  await waitFor(() => expect(platformFeaturesSaveEnvironment).toHaveBeenCalledWith('projects.reads', {
    defaultEnabled: false, forceDisabled: false, expectedVersion: 'environment-v1',
  }))

  await user.click(screen.getByRole('combobox', { name: 'Organization override for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Inherit environment' }))
  await user.click(screen.getByRole('button', { name: 'Save organization override for Project reads' }))
  await waitFor(() => expect(platformFeaturesSaveOrganization).toHaveBeenCalledWith('projects.reads', 'organization-1', {
    enabled: null, expectedVersion: 'organization-v1',
  }))
})

it('shows force-off precedence and keeps a control draft until an explicit conflict reload', async () => {
  const user = userEvent.setup()
  snapshot = [{ ...feature, deploymentDisabled: false, environmentControl: { ...feature.environmentControl, enabled: true, forceDisabled: true } }]
  vi.mocked(platformFeaturesSaveEnvironment)
    .mockRejectedValueOnce(Object.assign(new Error('The feature control changed.'), { status: 409 }))
    .mockResolvedValueOnce({ ...feature.environmentControl, enabled: false, version: 'environment-v2' })
  mount()
  expect(await screen.findByText('Forced off')).toBeInTheDocument()
  await user.click(screen.getByRole('combobox', { name: 'Environment control for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  await user.click(screen.getByRole('button', { name: 'Save environment control for Project reads' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Feature settings changed. Reload controls before saving again.')
  expect(platformFeaturesSaveEnvironment).toHaveBeenCalledTimes(1)
  expect(screen.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Disabled')

  snapshot = [{ ...snapshot[0], environmentControl: { ...snapshot[0].environmentControl, version: 'environment-v2' } }]
  await user.click(screen.getByRole('button', { name: 'Reload controls' }))
  await waitFor(() => expect(platformFeaturesList).toHaveBeenCalledTimes(2))
  expect(platformFeaturesSaveEnvironment).toHaveBeenCalledTimes(1)
  expect(screen.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Disabled')
  await user.click(screen.getByRole('button', { name: 'Save environment control for Project reads' }))
  await waitFor(() => expect(platformFeaturesSaveEnvironment).toHaveBeenLastCalledWith('projects.reads', {
    defaultEnabled: false, forceDisabled: true, expectedVersion: 'environment-v2',
  }))
})

it('keeps the selected value visible while a save is busy', async () => {
  permissions = ['platform.features.read', 'platform.features.manage', 'platform.tenants.read']
  const user = userEvent.setup()
  let finishSave!: (state: FeatureControlState) => void
  vi.mocked(platformFeaturesSaveEnvironment).mockImplementationOnce(() => new Promise<FeatureControlState>((resolve) => {
    finishSave = resolve
  }))
  mount()
  await screen.findByText('Project reads')
  await user.click(screen.getByRole('combobox', { name: 'Environment control for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  const save = screen.getByRole('button', { name: 'Save environment control for Project reads' })
  await user.click(save)
  await waitFor(() => expect(save).toBeDisabled())
  expect(screen.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Disabled')
  expect(screen.getByRole('combobox', { name: 'Organization override target' })).toBeDisabled()
  await act(async () => { finishSave({ ...feature.environmentControl, enabled: false, version: 'environment-v2' }) })
})

it('paginates immutable feature history and never exposes write controls to a reader', async () => {
  permissions = ['platform.features.read']
  const user = userEvent.setup()
  mount()
  await screen.findByText('Project reads')
  await user.click(screen.getByRole('button', { name: 'View history for Project reads' }))
  const dialog = within(await screen.findByRole('dialog'))
  expect(await dialog.findByText('actor-1')).toBeInTheDocument()
  await user.click(dialog.getByRole('button', { name: 'Next' }))
  await waitFor(() => expect(platformFeaturesHistory).toHaveBeenLastCalledWith('projects.reads', { page: 2, pageSize: 25 }, expect.anything()))
  expect(await dialog.findByText('actor-2')).toBeInTheDocument()
  expect(dialog.queryByRole('button', { name: /Save/ })).not.toBeInTheDocument()
})

it('renders a permission denial without requesting feature or tenant data', async () => {
  permissions = []
  mount()
  expect(await screen.findByText('You do not have permission to view feature controls.')).toBeInTheDocument()
  expect(platformFeaturesList).not.toHaveBeenCalled()
  expect(organizationsList).not.toHaveBeenCalled()
})

it('does not treat manage permission as permission to read feature data', async () => {
  permissions = ['platform.features.manage']
  mount()
  expect(await screen.findByText('You do not have permission to view feature controls.')).toBeInTheDocument()
  expect(platformFeaturesList).not.toHaveBeenCalled()
  expect(organizationsList).not.toHaveBeenCalled()
  expect(platformFeaturesSaveEnvironment).not.toHaveBeenCalled()
})

it('does not grant feature access from the legacy administrator session flag', async () => {
  vi.mocked(customFetch).mockResolvedValueOnce({ isPlatformAdministrator: true, platformPermissions: [] } as never)
  mount()
  expect(await screen.findByText('You do not have permission to view feature controls.')).toBeInTheDocument()
  expect(platformFeaturesList).not.toHaveBeenCalled()
  expect(organizationsList).not.toHaveBeenCalled()
})

it('reports a busy control separately and waits for an explicit reload before another save', async () => {
  const user = userEvent.setup()
  vi.mocked(platformFeaturesSaveEnvironment).mockRejectedValueOnce(Object.assign(new Error('The feature-control lock is busy.'), { status: 409, problem: { code: 'feature_control_busy', title: 'feature_control_busy' } }))
  mount()
  await screen.findByText('Project reads')
  await user.click(screen.getByRole('combobox', { name: 'Environment control for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  await user.click(screen.getByRole('button', { name: 'Save environment control for Project reads' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Feature controls are busy. Reload controls before saving again.')
  expect(platformFeaturesSaveEnvironment).toHaveBeenCalledTimes(1)
  await user.click(screen.getByRole('button', { name: 'Reload controls' }))
  await waitFor(() => expect(platformFeaturesList).toHaveBeenCalledTimes(2))
  expect(platformFeaturesSaveEnvironment).toHaveBeenCalledTimes(1)
  expect(screen.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Disabled')
})

it('keeps the version captured when editing even if a background refetch returns a newer version', async () => {
  const user = userEvent.setup()
  const client = mount()
  await screen.findByText('Project reads')
  await user.click(screen.getByRole('combobox', { name: 'Environment control for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))

  snapshot = [{ ...feature, environmentControl: { ...feature.environmentControl, version: 'environment-v2' } }]
  await client.invalidateQueries({ queryKey: ['platform', 'features'] })
  await waitFor(() => expect(platformFeaturesList).toHaveBeenCalledTimes(2))
  await waitFor(() => expect(screen.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Disabled'))
  await user.click(screen.getByRole('button', { name: 'Save environment control for Project reads' }))
  await waitFor(() => expect(platformFeaturesSaveEnvironment).toHaveBeenCalledWith('projects.reads', {
    defaultEnabled: false, forceDisabled: false, expectedVersion: 'environment-v1',
  }))
})

it('does not submit an organization override using a previous target snapshot', async () => {
  permissions = ['platform.features.read', 'platform.features.manage', 'platform.tenants.read']
  const user = userEvent.setup()
  vi.mocked(organizationsList).mockResolvedValue({ items: [
    { id: 'organization-1', name: 'Northwind', slug: 'northwind', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
    { id: 'organization-2', name: 'Southwind', slug: 'southwind', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
  ], page: 1, pageSize: 25, totalCount: 2 })
  vi.mocked(platformFeaturesList).mockImplementation(async params => ({
    environment: 'development', organizationId: params?.organizationId ?? null,
    items: params?.organizationId ? [{ ...feature, organizationControl: { ...feature.organizationControl, target: { featureKey: 'projects.reads', organizationId: params.organizationId }, version: params.organizationId === 'organization-2' ? 'organization-v2' : 'organization-v1' } }] : [{ ...feature, organizationControl: null }],
  }) as never)
  mount('organization-1')
  await screen.findByText('Project reads')
  await user.click(screen.getByRole('combobox', { name: 'Organization override for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  await user.click(screen.getByRole('combobox', { name: 'Organization override target' }))
  await user.click(await screen.findByRole('option', { name: 'Southwind' }))
  await waitFor(() => expect(platformFeaturesList).toHaveBeenLastCalledWith({ organizationId: 'organization-2' }, expect.anything()))
  await user.click(screen.getByRole('combobox', { name: 'Organization override for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  await user.click(screen.getByRole('button', { name: 'Save organization override for Project reads' }))
  await waitFor(() => expect(platformFeaturesSaveOrganization).toHaveBeenCalledWith('projects.reads', 'organization-2', {
    enabled: false, expectedVersion: 'organization-v2',
  }))
})

it('preserves a draft and shows a safe failure when a save is rejected outside a conflict', async () => {
  const user = userEvent.setup()
  vi.mocked(platformFeaturesSaveEnvironment).mockRejectedValueOnce(Object.assign(new Error('service unavailable'), { status: 503 }))
  mount()
  await screen.findByText('Project reads')
  await user.click(screen.getByRole('combobox', { name: 'Environment control for Project reads' }))
  await user.click(await screen.findByRole('option', { name: 'Disabled' }))
  await user.click(screen.getByRole('button', { name: 'Save environment control for Project reads' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Feature control could not be saved. Your selection is preserved.')
  expect(screen.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Disabled')
  expect(platformFeaturesSaveEnvironment).toHaveBeenCalledTimes(1)
})
