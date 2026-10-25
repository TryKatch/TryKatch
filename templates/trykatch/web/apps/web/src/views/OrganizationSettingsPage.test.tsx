import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { customFetch, organizationSettingsAiGet, organizationSettingsAiUpdate, organizationSettingsAiTest } from '@trykatch/api-client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { OrganizationSettingsPage } from './OrganizationSettingsPage'

const { quotasCurrentUsage } = vi.hoisted(() => ({ quotasCurrentUsage: vi.fn() }))
vi.mock('@trykatch/api-client', async original => ({ ...await original<typeof import('@trykatch/api-client')>(), customFetch: vi.fn(), organizationSettingsAiGet: vi.fn(), organizationSettingsAiUpdate: vi.fn(), organizationSettingsAiTest: vi.fn(), quotasCurrentUsage }))
const configuration = { enabled: false, version: '00000000-0000-0000-0000-000000000000', providerAvailable: true, provider: 'deepseek', model: 'test-model', endpoint: 'https://api.deepseek.com', timeoutMs: 30_000, hasApiKey: false, usesTenantProvider: false, canConfigureProvider: true, allowedEndpoints: ['https://api.deepseek.com'] }
beforeEach(() => {
  // JSDOM has no layout/scrolling; real focus and scrolling are covered by browser stories.
  HTMLElement.prototype.scrollIntoView = vi.fn()
  vi.mocked(customFetch).mockResolvedValue({ permissions: ['organizations.read', 'organizations.manage'] })
  vi.mocked(organizationSettingsAiGet).mockResolvedValue(configuration)
  vi.mocked(organizationSettingsAiUpdate).mockResolvedValue({ ...configuration, enabled: true, version: '00000000-0000-0000-0000-000000000001' })
  quotasCurrentUsage.mockResolvedValue({ resources: [] })
})
afterEach(() => { cleanup(); vi.resetAllMocks() })
function mount() { render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><OrganizationSettingsPage /></QueryClientProvider>) }

it('preserves activation-only compatibility until provider fields are edited', async () => {
  mount()
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Enable AI Help for this organization' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  await waitFor(() => expect(organizationSettingsAiUpdate).toHaveBeenCalledWith({ enabled: true, expectedVersion: configuration.version }))
  expect(await screen.findByText('Settings saved.')).toBeInTheDocument()
  expect(screen.getByRole('combobox', { name: 'Provider' })).toBeEnabled()
})
it('does not fetch settings or offer a save to unauthorized members', async () => {
  vi.mocked(customFetch).mockResolvedValue({ permissions: [] })
  mount()
  expect(await screen.findByText('You do not have permission to view organization settings.')).toBeInTheDocument()
  expect(organizationSettingsAiGet).not.toHaveBeenCalled()
  expect(organizationSettingsAiUpdate).not.toHaveBeenCalled()
})
it('changing provider clears the typed key and updates the destination controls', async () => {
  mount()
  fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'test-not-a-real-key' } })
  fireEvent.keyDown(screen.getByRole('combobox', { name: 'Provider' }), { key: 'Enter' })
  fireEvent.click(await screen.findByRole('option', { name: 'OpenAI' }))
  expect(screen.getByRole('combobox', { name: 'Provider' })).toHaveTextContent('OpenAI')
  expect(screen.getByLabelText('API key')).toHaveValue('')
  expect(screen.queryByLabelText('API endpoint')).not.toBeInTheDocument()
  expect(organizationSettingsAiUpdate).not.toHaveBeenCalled()
})
it('keeps readers read-only', async () => {
  vi.mocked(customFetch).mockResolvedValue({ permissions: ['organizations.read'] })
  mount()
  expect(await screen.findByRole('checkbox')).toBeDisabled()
  expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument()
})
it('preserves the selected value after a failed save', async () => {
  vi.mocked(organizationSettingsAiUpdate).mockRejectedValue(new Error('Unavailable'))
  mount()
  fireEvent.click(await screen.findByRole('checkbox'))
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Your selection is preserved.')
  expect(screen.getByRole('checkbox')).toBeChecked()
})
it('requires an explicit successful reload after conflict', async () => {
  vi.mocked(organizationSettingsAiUpdate).mockRejectedValue({ status: 409 })
  mount()
  fireEvent.click(await screen.findByRole('checkbox'))
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Settings changed.')
  fireEvent.click(screen.getByRole('checkbox'))
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Reload settings' }))
  await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument())
  expect(organizationSettingsAiUpdate).toHaveBeenCalledTimes(1)
})

it('saves a write-only subscription key and clears it after submission', async () => {
  vi.mocked(organizationSettingsAiUpdate).mockResolvedValue({ ...configuration, hasApiKey: true, usesTenantProvider: true })
  mount()
  fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'test-not-a-real-key' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  await waitFor(() => expect(organizationSettingsAiUpdate).toHaveBeenCalledWith({ enabled: false, expectedVersion: configuration.version, provider: 'deepseek', model: 'test-model', endpoint: configuration.endpoint, timeoutMs: 30_000, apiKey: 'test-not-a-real-key', removeApiKey: false }))
  expect(await screen.findByLabelText('Replacement API key')).toHaveValue('')
  expect(screen.getByText('API key configured. The saved key cannot be displayed.')).toBeInTheDocument()
})
it('rejects an unapproved destination without submitting the key', async () => {
  mount()
  fireEvent.change(await screen.findByLabelText('API endpoint'), { target: { value: 'https://localhost' } })
  fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'test-not-a-real-key' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('approved endpoint')
  expect(organizationSettingsAiUpdate).not.toHaveBeenCalled()
})
it('removes the saved key explicitly while disabled', async () => {
  vi.mocked(organizationSettingsAiGet).mockResolvedValue({ ...configuration, hasApiKey: true, usesTenantProvider: true })
  mount()
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Remove saved API key (disable AI Help first)' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  await waitFor(() => expect(organizationSettingsAiUpdate).toHaveBeenCalledWith(expect.objectContaining({ removeApiKey: true, apiKey: undefined, enabled: false })))
})
it('tests only the saved version and does not include a key in the test request', async () => {
  vi.mocked(organizationSettingsAiGet).mockResolvedValue({ ...configuration, hasApiKey: true, usesTenantProvider: true })
  vi.mocked(organizationSettingsAiTest).mockResolvedValue({ connected: true })
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Test connection' }))
  await waitFor(() => expect(organizationSettingsAiTest).toHaveBeenCalledWith({ expectedVersion: configuration.version }))
  expect(await screen.findByText('Connection successful.')).toBeInTheDocument()
})
it('clears a submitted replacement key on server failure without clearing other edits', async () => {
  vi.mocked(organizationSettingsAiUpdate).mockRejectedValue(new Error('Unavailable'))
  mount()
  fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'test-not-a-real-key' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'Model' }), { target: { value: 'new-model' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Re-enter a new API key')
  expect(screen.getByLabelText('API key')).toHaveValue('')
  expect(screen.getByRole('textbox', { name: 'Model' })).toHaveValue('new-model')
})

it('switches between accessible settings tabs and keeps the AI draft intact', async () => {
  mount()
  const aiTab = await screen.findByRole('tab', { name: 'AI Configuration' })
  const activation = await screen.findByRole('checkbox', { name: 'Enable AI Help for this organization' })
  const model = screen.getByRole('textbox', { name: 'Model' })
  const apiKey = screen.getByLabelText('API key')
  fireEvent.change(model, { target: { value: 'draft-model' } })
  fireEvent.change(apiKey, { target: { value: 'draft-key-not-a-real-secret' } })
  fireEvent.click(activation)
  const usageTab = screen.getByRole('tab', { name: 'Usage' })
  fireEvent.click(usageTab)
  expect(usageTab).toHaveAttribute('aria-selected', 'true')
  expect(screen.getByRole('tabpanel', { name: 'Usage' })).toBeVisible()
  expect(screen.getByRole('tabpanel', { name: 'AI Configuration', hidden: true })).not.toBeVisible()
  fireEvent.click(aiTab)
  expect(activation).toBeChecked()
  expect(screen.getByRole('textbox', { name: 'Model' })).toHaveValue('draft-model')
  expect(screen.getByLabelText('API key')).toHaveValue('draft-key-not-a-real-secret')
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled()
})

it('loads usage only when the authorized Usage tab is active', async () => {
  const resources = [{ resourceKey: 'documents.storage-bytes', name: 'Storage', unit: 'Bytes', mode: 'Finite', limit: '100', version: '00000000-0000-0000-0000-000000000001', source: 'host-configuration', committed: '25', reserved: '5', reconciliationHeld: '2', state: 'Ready', installed: true }]
  quotasCurrentUsage.mockResolvedValue({ resources })
  mount()
  await screen.findByRole('tab', { name: 'Usage' })
  expect(quotasCurrentUsage).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('tab', { name: 'Usage' }))
  expect(await screen.findByRole('heading', { name: 'Storage' })).toBeVisible()
  expect(quotasCurrentUsage).toHaveBeenCalledTimes(1)
  expect(quotasCurrentUsage).toHaveBeenCalledWith(expect.objectContaining({ signal: expect.any(AbortSignal) }))
})

it('does not request usage for a caller without organization read access', async () => {
  vi.mocked(customFetch).mockResolvedValue({ permissions: [] })
  mount()
  expect(await screen.findByText('You do not have permission to view organization settings.')).toBeInTheDocument()
  expect(quotasCurrentUsage).not.toHaveBeenCalled()
})

it('shows a loading state while access is pending and does not request usage', async () => {
  vi.mocked(customFetch).mockReturnValue(new Promise(() => {}))
  mount()
  fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }))
  expect(await screen.findByRole('status')).toBeVisible()
  expect(screen.queryByText('You do not have permission to view organization usage.')).not.toBeInTheDocument()
  expect(quotasCurrentUsage).not.toHaveBeenCalled()
})

it('shows the access error and retry control when access checking fails', async () => {
  vi.mocked(customFetch).mockRejectedValue(new Error('Access unavailable'))
  mount()
  fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }))
  const panel = within(screen.getByRole('tabpanel', { name: 'Usage' }))
  expect(await panel.findByText('Settings access could not be checked.')).toBeVisible()
  expect(panel.getByRole('button', { name: 'Retry' })).toBeVisible()
  expect(screen.queryByText('You do not have permission to view organization usage.')).not.toBeInTheDocument()
  expect(quotasCurrentUsage).not.toHaveBeenCalled()
})

it('does not request usage from stale access data after access revalidation fails', async () => {
  vi.mocked(customFetch).mockRejectedValue(new Error('Access unavailable'))
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['access'], { permissions: ['organizations.read'] })
  render(<QueryClientProvider client={client}><OrganizationSettingsPage /></QueryClientProvider>)
  await within(screen.getByRole('tabpanel', { name: 'AI Configuration' })).findByText('Settings access could not be checked.')
  fireEvent.click(screen.getByRole('tab', { name: 'Usage' }))
  expect(within(screen.getByRole('tabpanel', { name: 'Usage' })).getByText('Settings access could not be checked.')).toBeVisible()
  expect(quotasCurrentUsage).not.toHaveBeenCalled()
})

it('allows organization managers to view usage without a separate read grant', async () => {
  vi.mocked(customFetch).mockResolvedValue({ permissions: ['organizations.manage'] })
  quotasCurrentUsage.mockResolvedValue({ resources: [{ resourceKey: 'resource', name: 'Managed resource', unit: 'Count', mode: 'Unlimited', limit: null, version: null, source: null, committed: '0', reserved: '0', reconciliationHeld: '0', state: 'Ready', installed: true }] })
  mount()
  fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }))
  expect(await screen.findByText('Managed resource')).toBeVisible()
  expect(quotasCurrentUsage).toHaveBeenCalledTimes(1)
})

it('moves tab focus with arrow and boundary keys', async () => {
  mount()
  const aiTab = await screen.findByRole('tab', { name: 'AI Configuration' })
  const usageTab = screen.getByRole('tab', { name: 'Usage' })
  aiTab.focus()
  fireEvent.keyDown(aiTab, { key: 'End' })
  expect(usageTab).toHaveFocus()
  expect(usageTab).toHaveAttribute('aria-selected', 'true')
  fireEvent.keyDown(usageTab, { key: 'ArrowLeft' })
  expect(aiTab).toHaveFocus()
  fireEvent.keyDown(aiTab, { key: 'ArrowRight' })
  expect(usageTab).toHaveFocus()
  fireEvent.keyDown(usageTab, { key: 'Home' })
  expect(aiTab).toHaveFocus()
})

it('uses exact BigInt arithmetic for long limits, zero and unlimited values', async () => {
  quotasCurrentUsage.mockResolvedValue({ resources: [
    { resourceKey: 'documents.storage-bytes', name: 'Large storage', unit: 'Bytes', mode: 'Finite', limit: '9007199254740993', version: '00000000-0000-0000-0000-000000000001', source: 'host-configuration', committed: '9007199254740994', reserved: '1', reconciliationHeld: '5', state: 'Ready', installed: true },
    { resourceKey: 'zero.count', name: 'Zero count', unit: 'Count', mode: 'Finite', limit: '0', version: '00000000-0000-0000-0000-000000000002', source: 'host-configuration', committed: '0', reserved: '0', reconciliationHeld: '0', state: 'Ready', installed: true },
    { resourceKey: 'unlimited.count', name: 'Unlimited count', unit: 'Count', mode: 'Unlimited', limit: null, version: '00000000-0000-0000-0000-000000000003', source: 'host-configuration', committed: '9223372036854775807', reserved: '0', reconciliationHeld: '0', state: 'Ready', installed: true },
  ] })
  mount()
  fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }))
  expect(await screen.findByText('9,007,199,254,741,000 bytes')).toBeVisible()
  expect(screen.getByText('7 bytes')).toBeVisible()
  const zeroResource = (await screen.findByText('Zero count')).closest('article')!
  expect(within(zeroResource).getAllByText('0 items')).toHaveLength(6)
  expect(screen.getByText('Unlimited usage')).toBeVisible()
})

it('keeps known aggregates visible when reconciliation-held usage is unknown', async () => {
  quotasCurrentUsage.mockResolvedValue({ resources: [{ resourceKey: 'documents.storage-bytes', name: 'Partial inventory', unit: 'Bytes', mode: 'Finite', limit: '100', version: '00000000-0000-0000-0000-000000000004', source: 'host-configuration', committed: '40', reserved: '6', reconciliationHeld: null, state: 'Ready', installed: true }] })
  mount()
  fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }))
  const resource = (await screen.findByText('Partial inventory')).closest('article')!
  expect(within(resource).getAllByText('Not available')).toHaveLength(2)
  expect(within(resource).getByText('40 bytes')).toBeVisible()
  expect(within(resource).getByText('6 bytes')).toBeVisible()
  expect(within(resource).queryByText('46 bytes')).not.toBeInTheDocument()
})

it('preserves an unavailable server state for a retired resource with incomplete usage', async () => {
  quotasCurrentUsage.mockResolvedValue({ resources: [{ resourceKey: 'documents.storage-bytes', name: 'Retired storage', unit: 'Bytes', mode: 'Finite', limit: '10', version: '00000000-0000-0000-0000-000000000005', source: 'host-configuration', committed: null, reserved: '2', reconciliationHeld: null, state: 'Unavailable', installed: false }] })
  mount()
  fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }))
  const resource = (await screen.findByText('Retired storage')).closest('article')!
  expect(within(resource).getByText('Unavailable')).toBeVisible()
  expect(within(resource).queryByText('Paused')).not.toBeInTheDocument()
  expect(within(resource).getAllByText('Not available')).toHaveLength(2)
  expect(within(resource).queryByText('8 bytes')).not.toBeInTheDocument()
})
