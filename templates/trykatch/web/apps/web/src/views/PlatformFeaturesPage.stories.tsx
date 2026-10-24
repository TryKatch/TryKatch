import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import type { FeatureAdministrationItem, FeatureAdministrationSnapshot } from '@trykatch/api-client'
import { PlatformFeaturesPage } from './PlatformFeaturesPage'

const organizationId = '00000000-0000-0000-0000-000000000001'
const environmentVersion = '00000000-0000-0000-0000-000000000101'
const environmentVersionAfterConflict = '00000000-0000-0000-0000-000000000102'
const environmentVersionAfterSave = '00000000-0000-0000-0000-000000000103'
const organizationVersion = '00000000-0000-0000-0000-000000000201'
const organizationVersionAfterSave = '00000000-0000-0000-0000-000000000202'
const featureKey = 'projects.reads'

interface FeatureControlState {
  environmentEnabled: boolean | null
  forceDisabled: boolean
  environmentVersion: string
  configuredDefault: boolean | null
  organizationControl: { organizationId: string; enabled: boolean | null; version: string } | null
}

function controlState(overrides: Partial<FeatureControlState> = {}): FeatureControlState {
  return {
    environmentEnabled: null,
    forceDisabled: false,
    environmentVersion,
    configuredDefault: true,
    organizationControl: null,
    ...overrides,
  }
}

function featureItem(state: FeatureControlState, targetOrganizationId: string | null): FeatureAdministrationItem {
  const organizationControl = targetOrganizationId && state.organizationControl?.organizationId === targetOrganizationId
    ? { target: { featureKey, organizationId: targetOrganizationId }, enabled: state.organizationControl.enabled, forceDisabled: false, version: state.organizationControl.version, updatedAtUtc: null }
    : null
  return {
    moduleId: 'projects',
    declaration: { key: featureKey, name: 'Project reads', description: 'View projects and their details.', defaultEnabled: true },
    deploymentDisabled: false,
    configuredDefault: state.configuredDefault,
    environmentControl: { target: { featureKey }, enabled: state.environmentEnabled, forceDisabled: state.forceDisabled, version: state.environmentVersion, updatedAtUtc: null },
    organizationControl,
  }
}

function featureSnapshotHandler(state: FeatureControlState) {
  return http.get('*/api/v1/platform/features', ({ request }) => {
    const targetOrganizationId = new URL(request.url).searchParams.get('organizationId')
    const snapshot: FeatureAdministrationSnapshot = {
      environment: 'development',
      organizationId: targetOrganizationId,
      items: [featureItem(state, targetOrganizationId)],
    }
    return HttpResponse.json(snapshot)
  })
}

function featureControlHandlers(state: FeatureControlState, options: { conflictOnce?: 'busy' | 'stale' } = {}) {
  let conflict = options.conflictOnce
  const snapshot = featureSnapshotHandler(state)
  const saveEnvironment = http.put('*/api/v1/platform/features/:featureKey/environment', async ({ request }) => {
    const body = await request.json() as { defaultEnabled: boolean | null; forceDisabled: boolean; expectedVersion: string }
    if (conflict) {
      const kind = conflict
      conflict = undefined
      if (kind === 'stale') {
        state.environmentEnabled = false
        state.environmentVersion = environmentVersionAfterConflict
        return HttpResponse.json({ code: 'feature_control_conflict', title: 'Feature settings changed.' }, { status: 409 })
      }
      return HttpResponse.json({ code: 'feature_control_busy', title: 'Feature controls are busy.' }, { status: 409 })
    }
    if (body.expectedVersion !== state.environmentVersion) {
      return HttpResponse.json({ code: 'feature_control_conflict', title: 'Feature settings changed.' }, { status: 409 })
    }
    state.environmentEnabled = body.defaultEnabled
    state.forceDisabled = body.forceDisabled
    state.environmentVersion = environmentVersionAfterSave
    return HttpResponse.json({ target: { featureKey }, enabled: state.environmentEnabled, forceDisabled: state.forceDisabled, version: state.environmentVersion, updatedAtUtc: null })
  })
  const saveOrganization = http.put('*/api/v1/platform/features/:featureKey/organizations/:targetOrganizationId', async ({ params, request }) => {
    const targetOrganizationId = String(params.targetOrganizationId)
    const body = await request.json() as { enabled: boolean | null; expectedVersion: string }
    if (!state.organizationControl || state.organizationControl.organizationId !== targetOrganizationId
      || body.expectedVersion !== state.organizationControl.version) {
      return HttpResponse.json({ code: 'feature_control_conflict', title: 'Feature settings changed.' }, { status: 409 })
    }
    state.organizationControl = { organizationId: targetOrganizationId, enabled: body.enabled, version: organizationVersionAfterSave }
    return HttpResponse.json({ target: { featureKey, organizationId: targetOrganizationId }, enabled: body.enabled, forceDisabled: false, version: organizationVersionAfterSave, updatedAtUtc: null })
  })
  return [snapshot, saveEnvironment, saveOrganization]
}

const session = (permissions = ['platform.features.read', 'platform.features.manage', 'platform.tenants.read']) =>
  http.get('*/api/v1/auth/session', () => HttpResponse.json({ displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, platformPermissions: permissions }))
const antiforgery = http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))
const authHandlers = (permissions?: string[]) => [session(permissions), antiforgery]
const tenantList = http.get('*/api/v1/tenants', () => HttpResponse.json({
  items: [{ id: organizationId, name: 'Northwind', slug: 'northwind', isActive: true, createdAt: '2026-01-01T00:00:00Z' }],
  page: 1, pageSize: 25, totalCount: 1,
}))
const historyPage = (page: number) => HttpResponse.json({
  items: [{ id: `00000000-0000-0000-0000-00000000030${page}`, target: { featureKey }, actorId: '00000000-0000-0000-0000-000000000401', previousVersion: null,
    version: page === 1 ? environmentVersion : environmentVersionAfterConflict, previousEnabled: null, enabled: page === 1, previousForceDisabled: null,
    forceDisabled: false, changedAtUtc: `2026-10-24T0${page}:00:00Z` }],
  page, pageSize: 25, totalCount: 26,
})
const historyHandler = http.get('*/api/v1/platform/features/:featureKey/history', ({ request }) =>
  historyPage(Number(new URL(request.url).searchParams.get('page') ?? 1)))

const defaultState = controlState()
const defaultApi = [...featureControlHandlers(defaultState), tenantList, historyHandler]

const meta = {
  title: 'Application UI/Platform feature controls',
  component: PlatformFeaturesPage,
  parameters: { msw: { handlers: { auth: authHandlers(), api: defaultApi } } },
} satisfies Meta<typeof PlatformFeaturesPage>
export default meta
type Story = StoryObj<typeof meta>

export const NoPlayDefaultEnvironment: Story = {}

export const ControlsAndHistory: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('Project reads')
    await userEvent.click(canvas.getByRole('button', { name: 'View history for Project reads' }))
    const dialog = within(await within(canvasElement.ownerDocument.body).findByRole('dialog'))
    await expect(await dialog.findByText('00000000-0000-0000-0000-000000000401')).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Next' }))
    await expect(await dialog.findByText('Page 2 of 2')).toBeVisible()
  },
}

export const OrganizationEnabled: Story = {
  args: { organizationId },
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({ organizationControl: { organizationId, enabled: true, version: organizationVersion } })), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Enabled', { selector: '.badge' })).toBeVisible()
    await expect(canvas.getByText('Organization override')).toBeVisible()
  },
}

export const OrganizationDisabled: Story = {
  args: { organizationId },
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({ organizationControl: { organizationId, enabled: false, version: organizationVersion } })), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Disabled', { selector: '.badge' })).toBeVisible()
    await expect(canvas.getByText('Organization override')).toBeVisible()
  },
}

export const OrganizationInheritsEnvironment: Story = {
  args: { organizationId },
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({
    environmentEnabled: true,
    organizationControl: { organizationId, enabled: null, version: organizationVersion },
  })), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Enabled', { selector: '.badge' })).toBeVisible()
    await expect(canvas.getByText('Environment control')).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Organization override for Project reads' })).toHaveTextContent('Inherit environment')
  },
}

export const OrganizationOverride: Story = {
  args: { organizationId },
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({ organizationControl: { organizationId, enabled: false, version: organizationVersion } })), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('Project reads')
    const select = canvas.getByRole('combobox', { name: 'Organization override for Project reads' })
    await userEvent.click(select)
    await userEvent.click(await within(canvasElement.ownerDocument.body).findByRole('option', { name: 'Enabled' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Save organization override for Project reads' }))
    await expect(await canvas.findByText('Settings saved.')).toHaveTextContent('Settings saved.')
    await waitFor(() => expect(canvas.getByText('Enabled', { selector: '.badge' })).toBeVisible())
  },
}

export const InheritedConfiguredDefault: Story = {
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({ configuredDefault: true })), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Enabled', { selector: '.badge' })).toBeVisible()
    await expect(canvas.getByText('Configured default')).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Inherit configured default')
  },
}

export const ForcedOffPrecedence: Story = {
  args: { organizationId },
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({
    environmentEnabled: true,
    forceDisabled: true,
    organizationControl: { organizationId, enabled: true, version: organizationVersion },
  })), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Forced off')).toBeVisible()
    await expect(canvas.getByText('Force disabled by operator')).toBeVisible()
    await expect(canvas.getByRole('checkbox', { name: 'Force feature off' })).toBeChecked()
  },
}

export const ConflictReload: Story = {
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({ environmentEnabled: false }), { conflictOnce: 'stale' }), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('Project reads')
    await userEvent.click(canvas.getByRole('combobox', { name: 'Environment control for Project reads' }))
    await userEvent.click(await within(canvasElement.ownerDocument.body).findByRole('option', { name: 'Enabled' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Save environment control for Project reads' }))
    await expect(await canvas.findByRole('alert')).toHaveTextContent('Feature settings changed. Reload controls before saving again.')
    await userEvent.click(canvas.getByRole('button', { name: 'Reload controls' }))
    await waitFor(() => expect(canvas.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Enabled'))
    await userEvent.click(canvas.getByRole('button', { name: 'Save environment control for Project reads' }))
    await expect(await canvas.findByText('Settings saved.')).toHaveTextContent('Settings saved.')
    await waitFor(() => expect(canvas.getByText('Enabled', { selector: '.badge' })).toBeVisible())
  },
}

export const BusyReload: Story = {
  parameters: { msw: { handlers: { api: [...featureControlHandlers(controlState({ environmentEnabled: false }), { conflictOnce: 'busy' }), tenantList, historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('Project reads')
    await userEvent.click(canvas.getByRole('combobox', { name: 'Environment control for Project reads' }))
    await userEvent.click(await within(canvasElement.ownerDocument.body).findByRole('option', { name: 'Enabled' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Save environment control for Project reads' }))
    await expect(await canvas.findByRole('alert')).toHaveTextContent('Feature controls are busy. Reload controls before saving again.')
    await userEvent.click(canvas.getByRole('button', { name: 'Reload controls' }))
    await waitFor(() => expect(canvas.getByRole('combobox', { name: 'Environment control for Project reads' })).toHaveTextContent('Enabled'))
    await userEvent.click(canvas.getByRole('button', { name: 'Save environment control for Project reads' }))
    await expect(await canvas.findByText('Settings saved.')).toHaveTextContent('Settings saved.')
  },
}

export const ReadOnly: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.features.read']), api: [...featureControlHandlers(controlState()), historyHandler] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('Project reads')
    await expect(canvas.getByText('Read-only access')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: /Save/ })).not.toBeInTheDocument()
  },
}

export const LoadFailure: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/features', () => new HttpResponse(null, { status: 503 })), tenantList] } } },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement).findByRole('alert')).toHaveTextContent('Feature controls could not be loaded.')
  },
}

export const PermissionDenied: Story = {
  parameters: { msw: { handlers: { auth: authHandlers([]), api: [] } } },
  play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByText('You do not have permission to view feature controls.')).toBeVisible() },
}

export const FrenchDark: Story = { globals: { locale: 'fr', theme: 'dark' } }
export const EnglishDark: Story = { globals: { locale: 'en', theme: 'dark' } }

export const Phone320: Story = {
  parameters: { viewport: { defaultViewport: 'phone' } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Project reads')).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Environment control for Project reads' })).toBeVisible()
    await expect(canvasElement.ownerDocument.defaultView!.innerWidth).toBe(320)
    await expect(canvasElement.ownerDocument.documentElement.scrollWidth).toBeLessThanOrEqual(320)
  },
}

export const Tablet768: Story = {
  args: { organizationId },
  parameters: {
    viewport: { defaultViewport: 'tablet' },
    msw: { handlers: { api: [...featureControlHandlers(controlState({ organizationControl: { organizationId, enabled: true, version: organizationVersion } })), tenantList, historyHandler] } },
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Project reads')).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Organization override for Project reads' })).toBeVisible()
    await expect(canvasElement.ownerDocument.defaultView!.innerWidth).toBe(768)
    await expect(canvasElement.ownerDocument.documentElement.scrollWidth).toBeLessThanOrEqual(768)
  },
}

export const Desktop1440: Story = {
  parameters: { viewport: { defaultViewport: 'desktop' } },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement).findByText('Project reads')).toBeVisible()
    await expect(canvasElement.ownerDocument.defaultView!.innerWidth).toBe(1440)
    await expect(canvasElement.ownerDocument.documentElement.scrollWidth).toBeLessThanOrEqual(1440)
  },
}

export const Loading: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/features', async () => { await delay('infinite'); return HttpResponse.json({}) }), tenantList] } } },
}
