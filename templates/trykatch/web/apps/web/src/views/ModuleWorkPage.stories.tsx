import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import { ModuleWorkPage } from './ModuleWorkPage'

const work = { workId: 'work-1', organizationId: 'org-1', jobKey: 'billing.reconcile', jobVersion: 1, state: 'queued', attempts: 0, operatorPaused: false, recoveryGeneration: 2, createdAtUtc: '2026-10-23T10:00:00Z', dueAtUtc: null, nextAttemptAtUtc: null, failureCode: null }
const session = (permissions: string[]) => http.get('*/api/v1/auth/session', () => HttpResponse.json({ displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, platformPermissions: permissions }))
const antiforgery = http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))
const rows = (items = [work]) => http.get('*/api/v1/platform/module-work', () => HttpResponse.json({ items, page: 1, pageSize: 50, totalCount: items.length }))
const authHandlers = (permissions: string[]) => [session(permissions), antiforgery]

const meta = { title: 'Application UI/Platform module work', component: ModuleWorkPage, parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read', 'platform.jobs.manage']), api: [rows()] } } } } satisfies Meta<typeof ModuleWorkPage>
export default meta
type Story = StoryObj<typeof meta>

export const PauseWork: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read', 'platform.jobs.manage']), api: [rows(), http.post('*/api/v1/platform/module-work/work-1/pause', async ({ request }) => {
    await expect(await request.json()).toEqual(expect.objectContaining({ expectedGeneration: 2 }))
    return HttpResponse.json({ workId: 'work-1', action: 'pause', recoveryGeneration: 3, operatorPaused: true })
  })] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('billing.reconcile')
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for billing.reconcile' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Pause' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(dialog.getByText(/Pause this work item/)).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Confirm pause' }))
    await expect(await canvas.findByText('Work item paused.')).toBeVisible()
  },
}

export const ResumeWork: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read', 'platform.jobs.manage']), api: [rows([{ ...work, state: 'paused', operatorPaused: true }]), http.post('*/api/v1/platform/module-work/work-1/resume', async ({ request }) => {
    await expect(await request.json()).toEqual(expect.objectContaining({ expectedGeneration: 2 }))
    return HttpResponse.json({ workId: 'work-1', action: 'resume', recoveryGeneration: 3, operatorPaused: false })
  })] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('billing.reconcile')
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for billing.reconcile' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Resume' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(dialog.getByText('Resume this work item?')).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Confirm resume' }))
    await expect(await canvas.findByText('Work item resumed.')).toBeVisible()
  },
}

export const RetryWork: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read', 'platform.jobs.manage']), api: [rows([{ ...work, state: 'terminal' }]), http.post('*/api/v1/platform/module-work/work-1/retry', async ({ request }) => {
    await expect(await request.json()).toEqual(expect.objectContaining({ expectedGeneration: 2 }))
    return HttpResponse.json({ workId: 'work-1', action: 'retry', recoveryGeneration: 3, operatorPaused: false })
  })] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('billing.reconcile')
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for billing.reconcile' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Retry' }))
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await expect(dialog.getByText('Queue this work item for another attempt?')).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Confirm retry' }))
    await expect(await canvas.findByText('Work item queued for retry.')).toBeVisible()
  },
}

export const FilterByStatus: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read']), api: [http.get('*/api/v1/platform/module-work', ({ request }) => {
    const params = new URL(request.url).searchParams
    if (params.get('state') === 'denied') {
      expect(params.get('page')).toBe('1')
      return HttpResponse.json({ items: [{ ...work, state: 'denied' }], page: 1, pageSize: 50, totalCount: 1 })
    }
    return HttpResponse.json({ items: [work], page: Number(params.get('page') ?? 1), pageSize: 50, totalCount: 51 })
  })] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('billing.reconcile')
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await expect(await canvas.findByText('Page 2 of 2')).toBeVisible()
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Filter by status' }))
    await userEvent.click(within(canvasElement.ownerDocument.body).getByRole('option', { name: 'denied' }))
    await expect(await canvas.findByText('Page 1 of 1')).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Filter by status' })).toHaveTextContent('denied')
  },
}

export const ReadOnly: Story = { parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read']), api: [rows()] } } }, play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  await canvas.findByText('billing.reconcile')
  await expect(canvas.getByText('Read-only access')).toBeVisible()
  await expect(canvas.queryByRole('button', { name: 'Actions for billing.reconcile' })).not.toBeInTheDocument()
} }
export const Empty: Story = { parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read']), api: [rows([])] } } }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByText('No platform work yet')).toBeVisible() } }
export const Loading: Story = { parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read']), api: [http.get('*/api/v1/platform/module-work', async () => { await delay('infinite'); return HttpResponse.json({}) })] } } } }
export const LoadFailure: Story = { parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read']), api: [http.get('*/api/v1/platform/module-work', () => new HttpResponse(null, { status: 503 }))] } } }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByRole('alert')).toHaveTextContent('Platform work could not be loaded.') } }
export const Conflict: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read', 'platform.jobs.manage']), api: [rows(), http.post('*/api/v1/platform/module-work/work-1/pause', () => new HttpResponse(null, { status: 409 }))] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('billing.reconcile')
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for billing.reconcile' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Pause' }))
    await userEvent.click(within(within(canvasElement.ownerDocument.body).getByRole('dialog')).getByRole('button', { name: 'Confirm pause' }))
    await expect(await canvas.findByRole('alert')).toHaveTextContent('Work item changed. Reload the list before trying again.')
    await userEvent.click(canvas.getByRole('button', { name: 'Reload list' }))
    await expect(canvas.getByText('billing.reconcile')).toBeVisible()
  },
}
export const ActionFailure: Story = {
  parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read', 'platform.jobs.manage']), api: [rows(), http.post('*/api/v1/platform/module-work/work-1/pause', () => new HttpResponse(null, { status: 503 }))] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('billing.reconcile')
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for billing.reconcile' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Pause' }))
    await userEvent.click(within(within(canvasElement.ownerDocument.body).getByRole('dialog')).getByRole('button', { name: 'Confirm pause' }))
    await expect(await canvas.findByRole('alert')).toHaveTextContent('The requested work action could not be completed.')
    await expect(canvas.getByText('billing.reconcile')).toBeVisible()
  },
}
export const French: Story = { globals: { locale: 'fr' }, parameters: { msw: { handlers: { auth: authHandlers(['platform.jobs.read']), api: [rows()] } } }, play: async ({ canvasElement }) => { await expect(await within(canvasElement).findByText('billing.reconcile')).toBeVisible() } }
