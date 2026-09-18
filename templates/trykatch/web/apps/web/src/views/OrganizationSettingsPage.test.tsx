import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { customFetch, organizationSettingsAiGet, organizationSettingsAiUpdate, organizationSettingsAiTest } from '@trykatch/api-client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { OrganizationSettingsPage } from './OrganizationSettingsPage'

vi.mock('@trykatch/api-client', async original => ({ ...await original<typeof import('@trykatch/api-client')>(), customFetch: vi.fn(), organizationSettingsAiGet: vi.fn(), organizationSettingsAiUpdate: vi.fn(), organizationSettingsAiTest: vi.fn() }))
const configuration = { enabled: false, version: '00000000-0000-0000-0000-000000000000', providerAvailable: true, provider: 'deepseek', model: 'test-model', endpoint: 'https://api.deepseek.com', timeoutMs: 30_000, hasApiKey: false, usesTenantProvider: false, canConfigureProvider: true, allowedEndpoints: ['https://api.deepseek.com'] }
beforeEach(() => {
  // JSDOM has no layout/scrolling; real focus and scrolling are covered by browser stories.
  HTMLElement.prototype.scrollIntoView = vi.fn()
  vi.mocked(customFetch).mockResolvedValue({ permissions: ['organizations.read', 'organizations.manage'] })
  vi.mocked(organizationSettingsAiGet).mockResolvedValue(configuration)
  vi.mocked(organizationSettingsAiUpdate).mockResolvedValue({ ...configuration, enabled: true, version: '00000000-0000-0000-0000-000000000001' })
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
