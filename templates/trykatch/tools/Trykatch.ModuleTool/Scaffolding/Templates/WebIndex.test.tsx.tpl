// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { __ENTITY__Dto } from '@__NPM_SCOPE__/api-client'
import { ModuleProvider, WebModuleCatalog } from '@__NPM_SCOPE__/module-sdk'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { __MODULE__Page, __MODULE_CAMEL__Module, __MODULE_CAMEL__Table } from './index'

const transport = vi.hoisted(() => vi.fn<(url: string, options?: RequestInit) => Promise<unknown>>())
vi.mock('@__NPM_SCOPE__/api-client', () => ({ customFetch: transport, __WEB_TEST_API_MOCKS__ }))

const record: __ENTITY__Dto = {
  id: '0199ca9e-3870-7000-8000-000000000001',
  __WEB_TEST_FIELDS__
  __WEB_TEST_WORKFLOW_FIELDS__
  createdAt: '2026-09-16T12:00:00Z', updatedAt: null,
  lifecycle: { status: 'Active', archivedAt: null, archivedBy: null, deletedAt: null, deletedBy: null, deletionReason: null },
  version: '0199ca9e-3870-7000-8000-000000000003',
}
const latest = { ...record, version: '0199ca9e-3870-7000-8000-000000000004' }
const clients: QueryClient[] = []

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  clients.push(client)
  render(<QueryClientProvider client={client}>
    <ModuleProvider catalog={new WebModuleCatalog([__MODULE_CAMEL__Module])}><__MODULE__Page /></ModuleProvider>
  </QueryClientProvider>)
}

async function openEditor() {
  fireEvent.click(await screen.findByRole('button', { name: /^Actions for / }))
  fireEvent.click(screen.getByRole('menuitem', { name: /^Edit$/ }))
  return screen.getByRole('dialog')
}

function changeFirstField(dialog: HTMLElement) {
  const input = dialog.querySelector('input, textarea, select')
  if (input instanceof HTMLInputElement && input.type === 'checkbox') fireEvent.click(input)
  else if (input instanceof HTMLSelectElement) {
    const alternative = Array.from(input.options).find(option => option.value !== '' && option.value !== input.value)
    if (!alternative) throw new Error('The generated select must expose another business value.')
    fireEvent.change(input, { target: { value: alternative.value } })
  }
  else if (input instanceof HTMLInputElement || input instanceof HTMLTextAreaElement) {
    const value = input instanceof HTMLInputElement && input.type === 'date' ? '2026-09-17'
      : input instanceof HTMLInputElement && input.type === 'datetime-local' ? '2026-09-17T12:00'
      : input instanceof HTMLInputElement && input.type === 'number' ? '2'
      : /^[0-9a-f-]{36}$/i.test(input.value) ? '0199ca9e-3870-7000-8000-000000000005'
      : /^\d+$/.test(input.value) ? '2' : 'B'
    fireEvent.change(input, { target: { value } })
  } else throw new Error('The generated editor must expose a business field.')
}

function submit(dialog: HTMLElement) {
  const form = dialog.querySelector('form')
  if (!form) throw new Error('The generated editor must contain a form.')
  fireEvent.submit(form)
}

beforeEach(() => {
  transport.mockReset()
  transport.mockImplementation(async url => {
    if (url === '/api/v1/access') return { permissions: ['__MODULE_ID__.read', '__MODULE_ID__.manage'] }
    if (url.startsWith('/api/v1/__RESOURCE__/page?')) return { items: [record], page: 1, pageSize: 25, hasMore: false }
    throw new Error(`Unexpected request: ${url}`)
  })
})
afterEach(() => { cleanup(); clients.splice(0).forEach(client => client.clear()) })

describe('__MODULE_CAMEL__Module', () => {
  it('declares the generated route and permission', () => {
    expect(__MODULE_CAMEL__Module.id).toBe('__MODULE_ID__')
    expect(__MODULE_CAMEL__Module.extensionPoints[0]).toBe(__MODULE_CAMEL__Table)
    expect(__MODULE_CAMEL__Module.routes[0]?.path).toBe('/__RESOURCE__')
    expect(__MODULE_CAMEL__Module.navigation[0]?.requiredPermission).toBe('__MODULE_ID__.read')
    expect(__MODULE_CAMEL__Module.archiveResources?.[0]?.managePermission).toBe('__MODULE_ID__.manage')
  })
})

describe('__MODULE__Page conflicts', () => {
  it('preserves entered values and retries only after an explicit version refresh', async () => {
    const writes: Record<string, unknown>[] = []
    let reads = 0
    const defaults = transport.getMockImplementation()!
    transport.mockImplementation(async (url, options) => {
      if (options?.method === 'PUT') {
        writes.push(JSON.parse(String(options.body)))
        if (writes.length === 1) throw Object.assign(new Error('Stale version'), { status: 409, problem: { code: 'stale_version' } })
        return latest
      }
      if (url === `/api/v1/__RESOURCE__/${record.id}`) { reads++; return latest }
      return defaults(url, options)
    })
    renderPage()
    const dialog = await openEditor()
    changeFirstField(dialog)
    submit(dialog)
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('This record changed')
    expect(within(dialog).getByRole('button', { name: /^Save$/ })).toBeDisabled()
    expect(reads).toBe(0)
    expect(writes).toHaveLength(1)
    expect(writes[0]).toMatchObject({ expectedVersion: record.version })
    expect(writes[0]).not.toMatchObject({ __WEB_TEST_FIELDS__ })

    fireEvent.click(within(dialog).getByRole('button', { name: 'Load latest version' }))
    await waitFor(() => expect(within(dialog).getByRole('button', { name: /^Save$/ })).toBeEnabled())
    expect(reads).toBe(1)
    submit(dialog)
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(writes).toHaveLength(2)
    expect(writes[1]).toEqual({ ...writes[0], expectedVersion: latest.version })
  })

  it('keeps the stale version blocked when refreshing fails', async () => {
    const writes: unknown[] = []
    const defaults = transport.getMockImplementation()!
    transport.mockImplementation(async (url, options) => {
      if (options?.method === 'PUT') {
        writes.push(JSON.parse(String(options.body)))
        throw Object.assign(new Error('Stale version'), { status: 409, problem: { code: 'stale_version' } })
      }
      if (url === `/api/v1/__RESOURCE__/${record.id}`) throw new Error('Refresh unavailable')
      return defaults(url, options)
    })
    renderPage()
    const dialog = await openEditor()
    changeFirstField(dialog)
    submit(dialog)
    await within(dialog).findByRole('alert')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Load latest version' }))
    expect(await within(dialog).findByText('Refresh unavailable')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: /^Save$/ })).toBeDisabled()
    expect(writes).toHaveLength(1)
  })

  it('does not reopen a cancelled editor when its pending refresh finishes', async () => {
    let finishRefresh: ((value: __ENTITY__Dto) => void) | undefined
    const pendingRefresh = new Promise<__ENTITY__Dto>(resolve => { finishRefresh = resolve })
    const defaults = transport.getMockImplementation()!
    transport.mockImplementation(async (url, options) => {
      if (options?.method === 'PUT') throw Object.assign(new Error('Stale version'), { status: 409, problem: { code: 'stale_version' } })
      if (url === `/api/v1/__RESOURCE__/${record.id}`) return pendingRefresh
      return defaults(url, options)
    })
    renderPage()
    const dialog = await openEditor()
    submit(dialog)
    await within(dialog).findByRole('alert')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Load latest version' }))
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Load latest version' })).toBeDisabled())
    fireEvent.click(within(dialog).getByRole('button', { name: /^Cancel$/ }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await act(async () => { finishRefresh!(latest); await pendingRefresh })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
