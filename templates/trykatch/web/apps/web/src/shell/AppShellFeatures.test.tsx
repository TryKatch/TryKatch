import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from '@tanstack/react-router'
import { customFetch, featuresCurrent, getWorkspaceCurrentQueryKey, workspaceCurrent, type DocumentDto } from '@trykatch/api-client'
import { DocumentsPage } from '@trykatch-modules/documents'
import { ModuleProvider } from '@trykatch/module-sdk'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../i18n/I18nProvider'
import { workspaceModules } from '../modules'
import { ModuleRouteBoundary } from '../features/availability/ModuleRouteBoundary'
import { AppShell } from './AppShell'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  customFetch: vi.fn(),
  featuresCurrent: vi.fn(),
  workspaceCurrent: vi.fn(),
}))

vi.mock('./useWorkspaceRealtimeLifecycle', () => ({ useWorkspaceRealtimeLifecycle: vi.fn() }))
vi.mock('../features/assistant/AssistantChat', () => ({
  AssistantChatProvider: ({ children }: { children: ReactNode }) => children,
  AssistantChat: () => null,
}))

const organizationA = 'organization-a'
const organizationB = 'organization-b'
const defaultPermissions = ['projects.read', 'projects.manage', 'documents.read', 'documents.manage']
const projectRoute = workspaceModules.routesFor('workspace').find((route) => route.path === '/projects')!
const documentsRoute = workspaceModules.routesFor('workspace').find((route) => route.path === '/documents')!
const document: DocumentDto = {
  id: 'document-1', title: 'Workspace runbook', description: 'Current recovery notes', fileName: 'runbook.pdf', documentType: 'report',
  createdAt: '2026-10-20T10:00:00Z', metadata: { updatedAt: null, mediaType: 'application/pdf', sizeBytes: 1024, sha256: 'A'.repeat(64) },
  lifecycle: { status: 'Active', archivedAt: null, archivedBy: null, deletedAt: null, deletedBy: null, deletionReason: null },
}
const otherOrganizationDocument: DocumentDto = { ...document, id: 'document-2', title: 'Beta operations guide', description: 'Beta workspace notes' }
let permissions: string[]
let accessOrganizationId: string
let workspaceOrganizationId: string
let featureResponseOrganizationId: string
let featureValues: Record<string, boolean>
let accessFailure: (Error & { status?: number }) | undefined
let workspaceFailure: (Error & { status?: number }) | undefined
let nextAccessResponse: Promise<ReturnType<typeof accessDto>> | undefined
let nextWorkspaceResponse: Promise<ReturnType<typeof workspaceDto>> | undefined
let nextDocumentsResponse: Promise<DocumentDto[]> | undefined

const accessDto = (organizationId: string) => ({ organizationId, userId: 'user-1', membershipId: `membership-${organizationId}`, permissions: [...permissions] })
const workspaceDto = (organizationId: string) => ({ organizationId, membershipId: `membership-${organizationId}`, permissions: [...permissions] })

beforeEach(() => {
  permissions = [...defaultPermissions]
  accessOrganizationId = organizationA
  workspaceOrganizationId = organizationA
  featureResponseOrganizationId = organizationA
  featureValues = { 'projects.reads': true, 'projects.writes': true }
  accessFailure = undefined
  workspaceFailure = undefined
  nextAccessResponse = undefined
  nextWorkspaceResponse = undefined
  nextDocumentsResponse = undefined
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.stubGlobal('scrollTo', () => {})
  vi.mocked(customFetch).mockImplementation(async (url, options) => {
    if (url === '/api/v1/auth/session') return { userId: 'user-1', displayName: 'Test User', email: 'test@example.test', isPlatformAdministrator: false }
    if (url === '/api/v1/access') {
      if (nextAccessResponse) {
        const pending = nextAccessResponse
        nextAccessResponse = undefined
        return await pending
      }
      if (accessFailure) throw accessFailure
      return accessDto(accessOrganizationId)
    }
    if (url === '/api/v1/documents/?lifecycle=active') {
      if (nextDocumentsResponse) {
        const pending = nextDocumentsResponse
        nextDocumentsResponse = undefined
        return await pending
      }
      return workspaceOrganizationId === organizationB ? [otherOrganizationDocument] : [document]
    }
    if (url === '/api/v1/documents/' && options?.method === 'POST') return { ...document, title: 'Authority-safe upload' }
    return undefined
  })
  vi.mocked(workspaceCurrent).mockImplementation(async () => {
    if (nextWorkspaceResponse) {
      const pending = nextWorkspaceResponse
      nextWorkspaceResponse = undefined
      return await pending as never
    }
    if (workspaceFailure) throw workspaceFailure
    return workspaceDto(workspaceOrganizationId) as never
  })
  vi.mocked(featuresCurrent).mockImplementation(async () => ({
    organizationId: featureResponseOrganizationId,
    evaluatedAtUtc: '2026-10-24T10:00:00Z',
    values: featureValues,
  }) as never)
})

afterEach(() => {
  cleanup()
  if (vi.isFakeTimers()) {
    vi.clearAllTimers()
    vi.useRealTimers()
  }
  vi.resetAllMocks()
  vi.unstubAllGlobals()
  localStorage.clear()
})

function mount(initialPath = '/projects') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000, gcTime: Infinity } } })
  const rootRoute = createRootRoute({ component: () => <ModuleProvider catalog={workspaceModules}><Outlet /></ModuleProvider> })
  const shellRoute = createRoute({ getParentRoute: () => rootRoute, id: '_workspace', component: AppShell })
  const projectsRouteForTest = createRoute({
    getParentRoute: () => shellRoute,
    path: '/projects',
    component: () => <ModuleRouteBoundary route={projectRoute}><p>Protected Projects outlet probe</p></ModuleRouteBoundary>,
  })
  const documentsRouteForTest = createRoute({
    getParentRoute: () => shellRoute,
    path: '/documents',
    component: () => <ModuleRouteBoundary route={documentsRoute}><DocumentsPage /></ModuleRouteBoundary>,
  })
  const routeTree = rootRoute.addChildren([shellRoute.addChildren([projectsRouteForTest, documentsRouteForTest])])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: [initialPath] }), defaultPendingMinMs: 0 })
  render(<I18nProvider><QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider></I18nProvider>)
  return { client }
}

async function openDocumentUploadDraft() {
  expect(await screen.findByText(document.title)).toBeInTheDocument()
  fireEvent.click(await screen.findByRole('button', { name: 'Upload document' }))
  const dialog = screen.getByRole('dialog', { name: 'Upload document' })
  const file = new File(['private runbook bytes'], 'runbook.pdf', { type: 'application/pdf' })
  fireEvent.change(within(dialog).getByLabelText('File'), { target: { files: [file] } })
  fireEvent.change(within(dialog).getByRole('textbox', { name: 'Document title' }), { target: { value: 'Authority-safe upload' } })
  fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Draft notes for the current workspace' } })
  return {
    file,
    staleForm: dialog.querySelector('form')!,
    staleSubmit: within(dialog).getByRole('button', { name: 'Upload document' }),
  }
}

function documentUploads() {
  return vi.mocked(customFetch).mock.calls.filter(([url, options]) => url === '/api/v1/documents/' && options?.method === 'POST')
}

describe('Projects availability in the actual workspace shell', () => {
  it('fails closed on mismatched access and workspace organizations without requesting features', async () => {
    accessOrganizationId = organizationA
    workspaceOrganizationId = organizationB
    featureResponseOrganizationId = organizationB
    mount()

    expect(await screen.findByRole('link', { name: 'Overview' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
    expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
    expect(featuresCurrent).not.toHaveBeenCalled()
  })

  it('requests the current organization snapshot and exposes the permitted Projects route when enabled', async () => {
    const { client } = mount()

    expect(await screen.findByRole('link', { name: 'Projects' })).toBeInTheDocument()
    expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()
    expect(featuresCurrent).toHaveBeenCalledTimes(1)
    expect(client.getQueryData(['organization-features', organizationA])).toMatchObject({ organizationId: organizationA })
  })

  it('removes the Projects navigation and protected route when the snapshot disables reads', async () => {
    featureValues = { 'projects.reads': false, 'projects.writes': true }
    mount()

    expect(await screen.findByRole('heading', { name: 'Feature unavailable' })).toBeInTheDocument()
    expect(featuresCurrent).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
    expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
  })

  it('drops the prior organization feature decision when access refetch fails after a workspace switch', async () => {
    const { client } = mount()
    expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()

    featureResponseOrganizationId = organizationB
    act(() => {
      client.setQueryData(getWorkspaceCurrentQueryKey(), workspaceDto(organizationB))
      client.setQueryData(['access'], accessDto(organizationB))
    })
    await waitFor(() => expect(client.getQueryData(['organization-features', organizationB])).toMatchObject({ organizationId: organizationB }))
    expect(await screen.findByRole('link', { name: 'Projects' })).toBeInTheDocument()

    accessFailure = new Error('Access unavailable')
    await act(async () => { await client.invalidateQueries({ queryKey: ['access'], exact: true }) })

    expect(client.getQueryState(['access'])?.status).toBe('error')
    expect(client.getQueryData(['organization-features', organizationB])).toMatchObject({ organizationId: organizationB })
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
      expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
    })
  })

  it.each(['access', 'workspace', 'both'] as const)(
    'recovers from a 429 %s query failure only after fresh matching access and workspace snapshots',
    async (failedQuery) => {
      const { client } = mount()
      expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()
      expect(await screen.findByRole('link', { name: 'Projects' })).toBeInTheDocument()
      const initialFeatureRequests = vi.mocked(featuresCurrent).mock.calls.length

      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
      workspaceOrganizationId = organizationB
      accessOrganizationId = organizationB
      featureResponseOrganizationId = organizationB
      if (failedQuery === 'access' || failedQuery === 'both') {
        accessFailure = Object.assign(new Error('Too many requests'), { status: 429 })
      }
      if (failedQuery === 'workspace' || failedQuery === 'both') {
        workspaceFailure = Object.assign(new Error('Too many requests'), { status: 429 })
      }

      await act(async () => {
        await client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true })
      })
      expect(workspaceCurrent).toHaveBeenCalled()

      await act(async () => {
        await client.invalidateQueries({ queryKey: ['access'], exact: true })
      })

      if (failedQuery === 'access' || failedQuery === 'both') {
        expect(client.getQueryState(['access'])?.status).toBe('error')
        expect(client.getQueryState(['access'])?.error).toMatchObject({ status: 429 })
      } else {
        expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB })
      }
      if (failedQuery === 'workspace' || failedQuery === 'both') {
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.status).toBe('error')
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.error).toMatchObject({ status: 429 })
      } else {
        expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB })
      }
      await waitFor(() => {
        expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
        expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
      })
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests)

      accessFailure = undefined
      workspaceFailure = undefined
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000)
      })

      await waitFor(() => {
        expect(screen.getByRole('link', { name: 'Projects' })).toBeInTheDocument()
        expect(screen.getByText('Protected Projects outlet probe')).toBeInTheDocument()
      })
      expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB })
      expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB })
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests + 1)
      expect(client.getQueryData(['organization-features', organizationB])).toMatchObject({ organizationId: organizationB })
    },
  )

  it.each(['access', 'workspace'] as const)(
    'does not automatically retry a 403 %s query failure on the 429 recovery interval',
    async (failedQuery) => {
      const { client } = mount()
      expect(await screen.findByText('Protected Projects outlet probe')).toBeInTheDocument()
      const initialFeatureRequests = vi.mocked(featuresCurrent).mock.calls.length

      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
      workspaceOrganizationId = organizationB
      accessOrganizationId = organizationB
      featureResponseOrganizationId = organizationB
      if (failedQuery === 'access') {
        accessFailure = Object.assign(new Error('Forbidden'), { status: 403 })
      } else {
        workspaceFailure = Object.assign(new Error('Forbidden'), { status: 403 })
      }

      await act(async () => {
        await client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true })
      })
      await act(async () => {
        await client.invalidateQueries({ queryKey: ['access'], exact: true })
      })

      const failedAccessRequests = vi.mocked(customFetch).mock.calls.filter(([url]) => url === '/api/v1/access').length
      const failedWorkspaceRequests = vi.mocked(workspaceCurrent).mock.calls.length
      if (failedQuery === 'access') {
        expect(client.getQueryState(['access'])?.status).toBe('error')
        expect(client.getQueryState(['access'])?.error).toMatchObject({ status: 403 })
        expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB })
      } else {
        expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB })
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.status).toBe('error')
        expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.error).toMatchObject({ status: 403 })
      }
      await waitFor(() => {
        expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
        expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
      })
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests)

      accessFailure = undefined
      workspaceFailure = undefined
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000)
      })

      await waitFor(() => {
        if (failedQuery === 'access') expect(client.getQueryState(['access'])?.status).toBe('error')
        else expect(client.getQueryState(getWorkspaceCurrentQueryKey())?.status).toBe('error')
        expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument()
        expect(screen.queryByText('Protected Projects outlet probe')).not.toBeInTheDocument()
      })
      expect(vi.mocked(customFetch).mock.calls.filter(([url]) => url === '/api/v1/access')).toHaveLength(failedAccessRequests)
      expect(vi.mocked(workspaceCurrent).mock.calls).toHaveLength(failedWorkspaceRequests)
      expect(featuresCurrent).toHaveBeenCalledTimes(initialFeatureRequests)
    },
  )

  it.each(['access', 'workspace'] as const)(
    'keeps a Documents upload draft through a same-organization pending %s refresh and submits it after authority recovers',
    async (pendingQuery) => {
      const { client } = mount('/documents')
      const { file, staleForm, staleSubmit } = await openDocumentUploadDraft()
      const previousAccessCalls = vi.mocked(customFetch).mock.calls.filter(([url]) => url === '/api/v1/access').length
      const previousWorkspaceCalls = vi.mocked(workspaceCurrent).mock.calls.length
      let resolveAccess!: (value: ReturnType<typeof accessDto>) => void
      let resolveWorkspace!: (value: ReturnType<typeof workspaceDto>) => void
      if (pendingQuery === 'access') {
        nextAccessResponse = new Promise((resolve) => { resolveAccess = resolve })
      } else {
        nextWorkspaceResponse = new Promise((resolve) => { resolveWorkspace = resolve })
      }

      let refresh!: Promise<void>
      act(() => {
        refresh = client.invalidateQueries({ queryKey: pendingQuery === 'access' ? ['access'] : getWorkspaceCurrentQueryKey(), exact: true })
      })
      await waitFor(() => {
        if (pendingQuery === 'access') {
          expect(vi.mocked(customFetch).mock.calls.filter(([url]) => url === '/api/v1/access')).toHaveLength(previousAccessCalls + 1)
        } else {
          expect(vi.mocked(workspaceCurrent).mock.calls).toHaveLength(previousWorkspaceCalls + 1)
        }
        expect(screen.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
        expect(screen.queryByText(document.title)).not.toBeInTheDocument()
      })

      fireEvent.click(staleSubmit)
      fireEvent.submit(staleForm)
      expect(documentUploads()).toHaveLength(0)

      await act(async () => {
        if (pendingQuery === 'access') resolveAccess(accessDto(organizationA))
        else resolveWorkspace(workspaceDto(organizationA))
        await refresh
      })

      const resumedDialog = await screen.findByRole('dialog', { name: 'Upload document' })
      expect(within(resumedDialog).getByRole('textbox', { name: 'Document title' })).toHaveValue('Authority-safe upload')
      expect(within(resumedDialog).getByRole('textbox', { name: 'Description' })).toHaveValue('Draft notes for the current workspace')
      expect(within(resumedDialog).getByText(file.name)).toBeInTheDocument()
      fireEvent.submit(resumedDialog.querySelector('form')!)
      await waitFor(() => expect(documentUploads()).toHaveLength(1))
      const body = documentUploads()[0][1]?.body
      expect(body).toBeInstanceOf(FormData)
      if (!(body instanceof FormData)) throw new Error('Expected multipart document upload')
      expect(body.get('title')).toBe('Authority-safe upload')
      expect(body.get('description')).toBe('Draft notes for the current workspace')
      expect(body.get('file')).toBe(file)
    },
  )

  it('keeps a Documents edit draft through a pending same-organization workspace refresh', async () => {
    const { client } = mount('/documents')
    expect(await screen.findByText(document.title)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: `Actions for ${document.title}` }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Edit' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit document' })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Document title' }), { target: { value: 'Edited recovery runbook' } })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Unsubmitted edit description' } })
    const staleForm = dialog.querySelector('form')!
    const staleSubmit = within(dialog).getByRole('button', { name: 'Save changes' })
    let resolveWorkspace!: (value: ReturnType<typeof workspaceDto>) => void
    nextWorkspaceResponse = new Promise((resolve) => { resolveWorkspace = resolve })
    let refresh!: Promise<void>
    act(() => { refresh = client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true }) })

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Edit document' })).not.toBeInTheDocument()
      expect(screen.queryByText(document.title)).not.toBeInTheDocument()
    })
    fireEvent.click(staleSubmit)
    fireEvent.submit(staleForm)
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === `/api/v1/documents/${document.id}` && options?.method === 'PUT')).toBe(false)

    await act(async () => {
      resolveWorkspace(workspaceDto(organizationA))
      await refresh
    })
    const resumedDialog = await screen.findByRole('dialog', { name: 'Edit document' })
    expect(within(resumedDialog).getByRole('textbox', { name: 'Document title' })).toHaveValue('Edited recovery runbook')
    expect(within(resumedDialog).getByRole('textbox', { name: 'Description' })).toHaveValue('Unsubmitted edit description')
    fireEvent.submit(resumedDialog.querySelector('form')!)
    await waitFor(() => expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === `/api/v1/documents/${document.id}` && options?.method === 'PUT')).toBe(true))
  })

  it('does not expose organization A document cache or draft after matching authority switches to organization B', async () => {
    const { client } = mount('/documents')
    const { staleForm, staleSubmit } = await openDocumentUploadDraft()
    let resolveAccess!: (value: ReturnType<typeof accessDto>) => void
    let resolveWorkspace!: (value: ReturnType<typeof workspaceDto>) => void
    let resolveDocuments!: (value: DocumentDto[]) => void
    nextWorkspaceResponse = new Promise((resolve) => { resolveWorkspace = resolve })
    workspaceOrganizationId = organizationB
    let workspaceRefresh!: Promise<void>
    act(() => { workspaceRefresh = client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true }) })
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
      expect(screen.queryByText(document.title)).not.toBeInTheDocument()
    })
    fireEvent.click(staleSubmit)
    fireEvent.submit(staleForm)
    expect(documentUploads()).toHaveLength(0)

    await act(async () => {
      resolveWorkspace(workspaceDto(organizationB))
      await workspaceRefresh
    })
    await waitFor(() => expect(client.getQueryData(getWorkspaceCurrentQueryKey())).toMatchObject({ organizationId: organizationB }))
    expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationA })
    await waitFor(() => {
      expect(screen.queryByText(document.title)).not.toBeInTheDocument()
      expect(screen.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
    })
    expect(screen.queryByText(otherOrganizationDocument.title)).not.toBeInTheDocument()

    nextAccessResponse = new Promise((resolve) => { resolveAccess = resolve })
    nextDocumentsResponse = new Promise((resolve) => { resolveDocuments = resolve })
    accessOrganizationId = organizationB
    featureResponseOrganizationId = organizationB
    let accessRefresh!: Promise<void>
    act(() => { accessRefresh = client.invalidateQueries({ queryKey: ['access'], exact: true }) })
    await act(async () => {
      resolveAccess(accessDto(organizationB))
      await accessRefresh
    })
    await waitFor(() => expect(client.getQueryData(['access'])).toMatchObject({ organizationId: organizationB }))

    await act(async () => { resolveDocuments([otherOrganizationDocument]) })
    expect(await screen.findByText(otherOrganizationDocument.title)).toBeInTheDocument()
    expect(screen.queryByText(document.title)).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
    expect(documentUploads()).toHaveLength(0)
  })

  it('clears an edit draft after a confirmed manage-permission denial and keeps it cleared after permission returns', async () => {
    const { client } = mount('/documents')
    expect(await screen.findByText(document.title)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: `Actions for ${document.title}` }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Edit' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit document' })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Document title' }), { target: { value: 'Edited runbook draft' } })
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Description' }), { target: { value: 'Updated notes, not yet saved' } })
    const staleSubmit = within(dialog).getByRole('button', { name: 'Save changes' })

    permissions = defaultPermissions.filter((permission) => permission !== 'documents.manage')
    nextAccessResponse = Promise.resolve(accessDto(organizationA))
    await act(async () => { await client.invalidateQueries({ queryKey: ['access'], exact: true }) })
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Edit document' })).not.toBeInTheDocument()
      expect(screen.getByText(document.title)).toBeInTheDocument()
    })
    fireEvent.click(staleSubmit)
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === `/api/v1/documents/${document.id}` && options?.method === 'PUT')).toBe(false)
    expect(screen.queryByRole('button', { name: 'Upload document' })).not.toBeInTheDocument()

    permissions = [...defaultPermissions]
    nextAccessResponse = Promise.resolve(accessDto(organizationA))
    await act(async () => { await client.invalidateQueries({ queryKey: ['access'], exact: true }) })
    expect(await screen.findByText(document.title)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Edit document' })).not.toBeInTheDocument()
    expect(screen.queryByText('Edited runbook draft')).not.toBeInTheDocument()
    expect(vi.mocked(customFetch).mock.calls.some(([url, options]) =>
      url === `/api/v1/documents/${document.id}` && options?.method === 'PUT')).toBe(false)
  })

  it('clears an upload draft after a workspace 403 and keeps it cleared when authority recovers', async () => {
    const { client } = mount('/documents')
    const { staleForm, staleSubmit } = await openDocumentUploadDraft()
    workspaceFailure = Object.assign(new Error('Forbidden'), { status: 403 })

    await act(async () => { await client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true }) })
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
      expect(screen.queryByText(document.title)).not.toBeInTheDocument()
    })
    fireEvent.click(staleSubmit)
    fireEvent.submit(staleForm)
    expect(documentUploads()).toHaveLength(0)

    workspaceFailure = undefined
    await act(async () => { await client.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey(), exact: true }) })
    expect(await screen.findByText(document.title)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Upload document' })).not.toBeInTheDocument()
    expect(screen.queryByText('Authority-safe upload')).not.toBeInTheDocument()
    expect(documentUploads()).toHaveLength(0)
  })
})
