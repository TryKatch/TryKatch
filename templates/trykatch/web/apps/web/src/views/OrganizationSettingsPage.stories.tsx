import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import { OrganizationSettingsPage } from './OrganizationSettingsPage'

const initial = { enabled: false, version: '00000000-0000-0000-0000-000000000000', providerAvailable: true, provider: 'deepseek', model: 'preview-model', endpoint: 'https://api.deepseek.com', timeoutMs: 30_000, hasApiKey: false, usesTenantProvider: false, canConfigureProvider: true, allowedEndpoints: ['https://api.deepseek.com', 'https://api.openai.com/v1'] }
const usage = { resources: [
  { resourceKey: 'documents.storage-bytes', name: 'Document storage', unit: 'Bytes', mode: 'Finite', limit: '9007199254740993', version: '00000000-0000-0000-0000-000000000001', source: 'host-configuration', committed: '9007199254740994', reserved: '1', reconciliationHeld: '5', state: 'Ready', installed: true },
  { resourceKey: 'projects.records', name: 'Projects', unit: 'Count', mode: 'Finite', limit: '0', version: '00000000-0000-0000-0000-000000000002', source: 'host-configuration', committed: '0', reserved: '0', reconciliationHeld: '0', state: 'Ready', installed: true },
  { resourceKey: 'assistant.requests', name: 'Assistant requests', unit: 'Count', mode: 'Unlimited', limit: null, version: '00000000-0000-0000-0000-000000000003', source: 'host-configuration', committed: '9223372036854775807', reserved: '0', reconciliationHeld: '0', state: 'Ready', installed: true },
] }
const usageStates = { resources: [
  { ...usage.resources[0], resourceKey: 'new.resource', name: 'Preparing resource', mode: 'Unconfigured', limit: null, committed: null, reserved: null, reconciliationHeld: null, state: 'Initializing' },
  { ...usage.resources[1], resourceKey: 'old.resource', name: 'Retained resource', mode: 'Finite', state: 'Paused', installed: false, committed: null, reconciliationHeld: '12' },
  { ...usage.resources[1], resourceKey: 'away.resource', name: 'Unavailable resource', mode: 'Disabled', state: 'Unavailable' },
] }
const auth = (permissions = ['organizations.read', 'organizations.manage']) => [http.get('*/api/v1/access', () => HttpResponse.json({ permissions })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))]
const usageHandlers = (handler: ReturnType<typeof http.get>) => [http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json(initial)), handler]
const retryUsageHandler = () => {
  let failed = false
  return http.get('*/api/v1/quotas/usage', () => {
    if (!failed) { failed = true; return new HttpResponse(null, { status: 503 }) }
    return HttpResponse.json(usage)
  })
}
const meta = {
  title: 'Application UI/Organization settings', component: OrganizationSettingsPage,
  parameters: { msw: { handlers: { auth: auth(), api: [
    http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json(initial)),
    http.get('*/api/v1/quotas/usage', () => HttpResponse.json(usage)),
    http.put('*/api/v1/organization-settings/ai', async ({ request }) => {
      await expect(await request.json()).toEqual({ enabled: true, expectedVersion: initial.version })
      return HttpResponse.json({ ...initial, enabled: true, version: '00000000-0000-0000-0000-000000000001' })
    }),
  ] } } },
} satisfies Meta<typeof OrganizationSettingsPage>
export default meta
type Story = StoryObj<typeof meta>

export const ActivateForOrganization: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByRole('tab', { name: 'AI Configuration' })).toHaveAttribute('aria-selected', 'true')
    const optIn = await canvas.findByRole('checkbox', { name: 'Enable AI Help for this organization' })
    await userEvent.click(optIn)
    await userEvent.click(canvas.getByRole('button', { name: 'Save changes' }))
    await expect(await canvas.findByText('Settings saved.')).toBeVisible()
    await expect(optIn).toBeChecked()
  },
}
export const ReadOnly: Story = { parameters: { msw: { handlers: { auth: auth(['organizations.read']) } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByRole('checkbox')).toBeDisabled()
    await expect(canvas.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument()
    await expect(canvas.getByRole('combobox', { name: 'Provider' })).toBeDisabled()
  },
}
export const ProviderSelection: Story = { play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await userEvent.type(await canvas.findByLabelText('API key'), 'storybook-not-a-real-key')
  await userEvent.click(canvas.getByRole('combobox', { name: 'Provider' }))
  await userEvent.click(await within(canvasElement.ownerDocument.body).findByRole('option', { name: 'OpenAI' }))
  await expect(canvas.getByRole('combobox', { name: 'Provider' })).toHaveTextContent('OpenAI')
  await expect(canvas.getByLabelText('API key')).toHaveValue('')
  await expect(canvas.queryByLabelText('API endpoint')).not.toBeInTheDocument()
} }
export const CompactFloatingControls: Story = { play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await canvas.findByLabelText('API key')
  const controls = [...canvasElement.querySelectorAll<HTMLInputElement | HTMLButtonElement>('.ai-settings-fields .floating-control-input')]
  await expect(controls.length).toBe(5)
  for (const control of controls) {
    await expect(control.getBoundingClientRect().height).toBe(38)
    control.focus()
    await expect(getComputedStyle(control).outlineStyle).toBe('none')
    await expect(getComputedStyle(control).boxShadow).toBe('none')
    const label = control.parentElement!.querySelector('label')!
    await expect(getComputedStyle(label).backgroundColor).toBe('rgba(0, 0, 0, 0)')
    await waitFor(() => expect(label.getBoundingClientRect().top).toBeLessThan(control.getBoundingClientRect().top))
  }
} }
export const ProviderNotConfigured: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json({ ...initial, providerAvailable: false, provider: '', model: '' }))] } } }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByRole('checkbox')).toBeVisible() } }
export const Loading: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/organization-settings/ai', async () => { await delay('infinite'); return HttpResponse.json(initial) })] } } } }
export const LoadFailure: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/organization-settings/ai', () => new HttpResponse(null, { status: 503 }))] } } }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByRole('alert')).toHaveTextContent('Settings could not be loaded.') } }
export const SaveFailure: Story = { parameters: { msw: { handlers: { api: [
  http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json(initial)),
  http.put('*/api/v1/organization-settings/ai', () => new HttpResponse(null, { status: 503 })),
] } } }, play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await userEvent.click(await canvas.findByRole('checkbox'))
  await userEvent.click(canvas.getByRole('button', { name: 'Save changes' }))
  await expect(await canvas.findByRole('alert')).toHaveTextContent('Your selection is preserved.')
  await expect(canvas.getByRole('checkbox')).toBeChecked()
} }
export const Conflict: Story = { parameters: { msw: { handlers: { api: [
  http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json(initial)),
  http.put('*/api/v1/organization-settings/ai', () => new HttpResponse(null, { status: 409 })),
] } } }, play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await userEvent.click(await canvas.findByRole('checkbox'))
  await userEvent.click(canvas.getByRole('button', { name: 'Save changes' }))
  await expect(await canvas.findByRole('alert')).toHaveTextContent('Settings changed.')
  await expect(canvas.getByRole('button', { name: 'Save changes' })).toBeDisabled()
  await userEvent.click(canvas.getByRole('button', { name: 'Reload settings' }))
  await waitFor(() => expect(canvas.getByRole('checkbox')).not.toBeChecked())
} }
export const DarkFrench: Story = { globals: { theme: 'dark', locale: 'fr' }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByRole('checkbox', { name: 'Activer l’aide IA pour cette organisation' })).toBeVisible() } }
export const PermissionDenied: Story = { parameters: { msw: { handlers: { auth: auth([]), api: [] } } }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByText('You do not have permission to view organization settings.')).toBeVisible() } }

export const UsageOverview: Story = { play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' }))
  await expect(await canvas.findByText('Document storage')).toBeVisible()
  await expect(canvas.getByText('9,007,199,254,741,000 bytes')).toBeVisible()
  await expect(canvas.getByText('7 bytes')).toBeVisible()
  await expect(canvas.getByText(/over limit/i)).toBeVisible()
  await expect(canvas.getByText('Unlimited usage')).toBeVisible()
} }
export const UsageEmpty: Story = { parameters: { msw: { handlers: { api: usageHandlers(http.get('*/api/v1/quotas/usage', () => HttpResponse.json({ resources: [] }))) } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByText('No usage to show')).toBeVisible() } }
export const UsageLoading: Story = { parameters: { msw: { handlers: { api: usageHandlers(http.get('*/api/v1/quotas/usage', async () => { await delay('infinite'); return HttpResponse.json(usage) })) } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByRole('status', { name: 'Loading usage' })).toBeVisible() } }
export const UsageFailureRetry: Story = { parameters: { msw: { handlers: { api: usageHandlers(retryUsageHandler()) } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByRole('alert')).toHaveTextContent('Usage could not be loaded.'); await userEvent.click(canvas.getByRole('button', { name: 'Retry' })); await expect(await canvas.findByText('Document storage')).toBeVisible() } }
export const UsageResourceStates: Story = { parameters: { msw: { handlers: { api: usageHandlers(http.get('*/api/v1/quotas/usage', () => HttpResponse.json(usageStates))) } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByText('Preparing resource')).toBeVisible(); await expect(canvas.getByText('Retained resource')).toBeVisible(); await expect(canvas.getByText('Unavailable resource')).toBeVisible(); await expect(canvas.getByText('12 items')).toBeVisible() } }
export const UsagePermissionDenied: Story = { parameters: { msw: { handlers: { auth: auth([]), api: [] } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByText('You do not have permission to view organization usage.')).toBeVisible() } }
export const UsageAccessFailure: Story = { parameters: { msw: { handlers: { auth: [http.get('*/api/v1/access', () => new HttpResponse(null, { status: 503 })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))] } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); const panel = within(canvas.getByRole('tabpanel', { name: 'Usage' })); await expect(await panel.findByRole('alert')).toHaveTextContent('Settings access could not be checked.'); await expect(panel.getByRole('button', { name: 'Retry' })).toBeVisible() } }
export const UsageFrenchDark: Story = { globals: { theme: 'dark', locale: 'fr' }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Utilisation' })); await expect(await canvas.findByText('Document storage')).toBeVisible(); await expect(canvas.getByText('Utilisation illimitée')).toBeVisible() } }
export const UsagePhone320: Story = { globals: { viewport: { value: 'phone', isRotated: false } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByText('Document storage')).toBeVisible() } }
export const UsageTablet768: Story = { globals: { viewport: { value: 'tablet', isRotated: false } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByText('Document storage')).toBeVisible() } }
export const UsageDesktop1440: Story = { globals: { viewport: { value: 'desktop', isRotated: false } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('tab', { name: 'Usage' })); await expect(await canvas.findByText('Document storage')).toBeVisible() } }

export const ConfigureSubscription: Story = { parameters: { msw: { handlers: { api: [
  http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json(initial)),
  http.put('*/api/v1/organization-settings/ai', async ({ request }) => {
    await expect(await request.json()).toEqual({ enabled: true, expectedVersion: initial.version, provider: 'deepseek', model: 'subscription-model', endpoint: initial.endpoint, timeoutMs: 30_000, apiKey: 'storybook-not-a-real-key', removeApiKey: false })
    return HttpResponse.json({ ...initial, enabled: true, model: 'subscription-model', hasApiKey: true, usesTenantProvider: true, version: '00000000-0000-0000-0000-000000000002' })
  }),
  http.post('*/api/v1/organization-settings/ai/test', () => HttpResponse.json({ connected: true })),
] } } }, play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await userEvent.type(await canvas.findByLabelText('API key'), 'storybook-not-a-real-key')
  await userEvent.clear(canvas.getByRole('textbox', { name: 'Model' }))
  await userEvent.type(canvas.getByRole('textbox', { name: 'Model' }), 'subscription-model')
  await userEvent.click(canvas.getByRole('checkbox', { name: 'Enable AI Help for this organization' }))
  await userEvent.click(canvas.getByRole('button', { name: 'Save changes' }))
  await expect(await canvas.findByText('Settings saved.')).toBeVisible()
  await expect(canvas.getByLabelText('Replacement API key')).toHaveValue('')
  await userEvent.click(canvas.getByRole('button', { name: 'Test connection' }))
  await expect(await canvas.findByText('Connection successful.')).toBeVisible()
} }
export const KeyConfigured: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json({ ...initial, usesTenantProvider: true, hasApiKey: true }))] } } }, play: async ({ canvasElement, globals }) => { await expect(await within(canvasElement).findByLabelText(globals.locale === 'fr' ? 'Clé API de remplacement' : 'Replacement API key')).toHaveValue('') } }
export const ConnectionFailure: Story = { parameters: { msw: { handlers: { api: [
  http.get('*/api/v1/organization-settings/ai', () => HttpResponse.json({ ...initial, usesTenantProvider: true, hasApiKey: true })),
  http.post('*/api/v1/organization-settings/ai/test', () => new HttpResponse(null, { status: 502 })),
] } } }, play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(await canvas.findByRole('button', { name: 'Test connection' })); await expect(await canvas.findByRole('alert')).toHaveTextContent('Connection failed.') } }
