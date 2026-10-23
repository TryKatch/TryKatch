import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { customFetch, moduleWorkList, moduleWorkPause, moduleWorkResume, moduleWorkRetry } from '@trykatch/api-client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ModuleWorkPage } from './ModuleWorkPage'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  customFetch: vi.fn(), moduleWorkList: vi.fn(), moduleWorkPause: vi.fn(), moduleWorkResume: vi.fn(), moduleWorkRetry: vi.fn(),
}))

const row = { workId: 'work-1', organizationId: 'org-1', jobKey: 'billing.reconcile', jobVersion: 1, state: 'queued', attempts: 0, operatorPaused: false, recoveryGeneration: 2, createdAtUtc: '2026-10-23T10:00:00Z', dueAtUtc: null, nextAttemptAtUtc: null, failureCode: null }
let permissions: string[]
let page: { items: typeof row[]; page: number; pageSize: number; totalCount: number }
const originalScrollIntoView = Element.prototype.scrollIntoView

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn()
  permissions = ['platform.jobs.read', 'platform.jobs.manage']
  page = { items: [row], page: 1, pageSize: 50, totalCount: 1 }
  vi.mocked(customFetch).mockImplementation(async (url) => {
    if (url === '/api/v1/auth/session') return { displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, platformPermissions: permissions } as never
    return undefined as never
  })
  vi.mocked(moduleWorkList).mockImplementation(async () => page)
  vi.mocked(moduleWorkPause).mockResolvedValue({ workId: row.workId, action: 'pause', recoveryGeneration: 3, operatorPaused: true })
  vi.mocked(moduleWorkResume).mockResolvedValue({ workId: row.workId, action: 'resume', recoveryGeneration: 3, operatorPaused: false })
  vi.mocked(moduleWorkRetry).mockResolvedValue({ workId: row.workId, action: 'retry', recoveryGeneration: 3, operatorPaused: false })
})
afterEach(() => { cleanup(); vi.clearAllMocks(); Element.prototype.scrollIntoView = originalScrollIntoView })

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><ModuleWorkPage /></QueryClientProvider>)
}

it('shows work and requires explicit confirmation before pausing it', async () => {
  mount()
  expect(await screen.findByText('billing.reconcile')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Actions for billing.reconcile' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Pause' }))
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByText(/Pause this work item/)).toBeInTheDocument()
  fireEvent.click(dialog.getByRole('button', { name: 'Confirm pause' }))
  await waitFor(() => expect(moduleWorkPause).toHaveBeenCalledWith('work-1', expect.objectContaining({ expectedGeneration: 2, requestId: expect.any(String) })))
})

it.each([
  { action: 'resume', row: { ...row, state: 'paused', operatorPaused: true }, confirm: 'Confirm resume', success: 'Work item resumed.', mutation: moduleWorkResume },
  { action: 'retry', row: { ...row, state: 'terminal' }, confirm: 'Confirm retry', success: 'Work item queued for retry.', mutation: moduleWorkRetry },
] as const)('confirms $action against the matching endpoint using the displayed generation', async ({ action, row: actionRow, confirm, success, mutation }) => {
  page = { ...page, items: [actionRow] }
  mount()
  expect(await screen.findByText('billing.reconcile')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Actions for billing.reconcile' }))
  fireEvent.click(screen.getByRole('menuitem', { name: action === 'resume' ? 'Resume' : 'Retry' }))
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByText(action === 'resume' ? 'Resume this work item?' : 'Queue this work item for another attempt?')).toBeInTheDocument()
  fireEvent.click(dialog.getByRole('button', { name: confirm }))
  await waitFor(() => expect(mutation).toHaveBeenCalledWith('work-1', expect.objectContaining({ expectedGeneration: 2, requestId: expect.any(String) })))
  expect(await screen.findByRole('status')).toHaveTextContent(success)
})

it('keeps the list read-only without the manage permission', async () => {
  permissions = ['platform.jobs.read']
  mount()
  expect(await screen.findByText('billing.reconcile')).toBeInTheDocument()
  expect(screen.getByText('Read-only access')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Actions for billing.reconcile' })).not.toBeInTheDocument()
})

it('filters by status and returns to the first page', async () => {
  page = { ...page, totalCount: 51 }
  mount()
  await screen.findByText('billing.reconcile')
  fireEvent.click(screen.getByRole('button', { name: 'Next' }))
  await waitFor(() => expect(moduleWorkList).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, pageSize: 50 })))
  fireEvent.keyDown(await screen.findByRole('combobox', { name: 'Filter by status' }), { key: 'ArrowDown', code: 'ArrowDown' })
  fireEvent.click(await screen.findByRole('option', { name: 'denied' }))
  await waitFor(() => expect(moduleWorkList).toHaveBeenLastCalledWith({ page: 1, pageSize: 50, state: 'denied' }))
})

it('shows an empty state and a conflict response without hiding the work item', async () => {
  page = { ...page, items: [], totalCount: 0 }
  mount()
  expect(await screen.findByText('No platform work yet')).toBeInTheDocument()
  vi.mocked(moduleWorkList).mockResolvedValue({ ...page, items: [row], totalCount: 1 })
  // A separate render exercises conflict handling against the returned row.
  cleanup()
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  vi.mocked(customFetch).mockImplementation(async (url, options) => {
    if (url === '/api/v1/auth/session') return { displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, platformPermissions: permissions } as never
    return undefined as never
  })
  vi.mocked(moduleWorkList).mockResolvedValue({ ...page, items: [row], totalCount: 1 })
  vi.mocked(moduleWorkPause).mockRejectedValue(Object.assign(new Error('Work item changed.'), { status: 409 }))
  render(<QueryClientProvider client={client}><ModuleWorkPage /></QueryClientProvider>)
  await screen.findByText('billing.reconcile')
  fireEvent.click(screen.getByRole('button', { name: 'Actions for billing.reconcile' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Pause' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Confirm pause' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Work item changed. Reload the list before trying again.')
  expect(screen.getByText('billing.reconcile')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Reload list' })).toBeEnabled()
})
