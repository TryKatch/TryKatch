import { QueryClient, QueryObserver } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { authenticationAntiforgery, getWorkspaceCurrentQueryKey, realtimeGetHistory, realtimeGetState, setAntiforgeryToken } from '@trykatch/api-client'
import { clearOrganizationDataForReset, clearOrganizationQueryState, createWorkspaceRealtimeLifecycle } from './realtimeLifecycle'

const signalr = vi.hoisted(() => {
  const connection = {
    on: vi.fn(), onreconnected: vi.fn(), onclose: vi.fn(), start: vi.fn(async () => undefined),
    stop: vi.fn(async () => undefined), invoke: vi.fn(async () => undefined),
  }
  const state = { url: '', options: undefined as unknown, connection }
  const requests: Array<{ headers?: Record<string, string>; abortSignal?: unknown; timeout?: number }> = []
  return { connection, state, requests }
})

vi.mock('@microsoft/signalr', () => ({
  DefaultHttpClient: class {
    async send(request: { headers?: Record<string, string>; abortSignal?: unknown; timeout?: number }) {
      signalr.requests.push(request)
      return { statusCode: 200 }
    }
  },
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) { signalr.state.url = url; signalr.state.options = options; return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() { return signalr.connection }
  },
  HttpTransportType: { WebSockets: 1 },
  LogLevel: { None: 6 },
}))
vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  authenticationAntiforgery: vi.fn(),
  realtimeGetHistory: vi.fn(),
  realtimeGetState: vi.fn(),
  setAntiforgeryToken: vi.fn(),
}))

beforeEach(() => {
  vi.mocked(authenticationAntiforgery).mockResolvedValue({ token: 'csrf-secret' })
  vi.mocked(realtimeGetState).mockResolvedValue({ enabled: true, generation: 'server-generation', cursor: 'head' })
  vi.mocked(realtimeGetHistory).mockResolvedValue({ items: [], generation: 'server-generation', cursor: 'next', hasMore: false })
  signalr.connection.start.mockReset().mockResolvedValue(undefined)
  signalr.connection.stop.mockReset().mockResolvedValue(undefined)
  signalr.connection.invoke.mockReset().mockResolvedValue(undefined)
  signalr.connection.on.mockClear()
  signalr.requests.length = 0
  signalr.state.url = ''
  signalr.state.options = undefined
})

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('workspace realtime lifecycle', () => {
  it('uses cookie WebSockets, anti-forgery negotiation and module-owned subscriptions', async () => {
    const lifecycle = createWorkspaceRealtimeLifecycle(new QueryClient())

    await lifecycle.start()
    await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalledWith('ReplaceSubscriptions', [
      { policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 }, kind: 0 },
    ]))

    expect(signalr.state.url).toBe('/api/v1/realtime/hub')
    const options = signalr.state.options as { transport: number; withCredentials: boolean; accessTokenFactory?: unknown; skipNegotiation?: boolean; httpClient: { send(request: unknown): Promise<unknown> } }
    expect(options).toMatchObject({ transport: 1, withCredentials: true })
    expect(options.accessTokenFactory).toBeUndefined()
    expect(options.skipNegotiation).toBeUndefined()
    const abortSignal = { aborted: false }
    await options.httpClient.send({ method: 'POST', url: '/api/v1/realtime/hub/negotiate', withCredentials: true, abortSignal, timeout: 10_000 } as never)
    expect(authenticationAntiforgery).toHaveBeenCalledOnce()
    expect(setAntiforgeryToken).toHaveBeenCalledWith('csrf-secret')
    expect(signalr.requests[0]).toMatchObject({ headers: { 'X-CSRF-TOKEN': 'csrf-secret' }, abortSignal, timeout: 10_000 })
    await lifecycle.stop()
    expect(signalr.connection.stop).toHaveBeenCalledOnce()
  })

  it('stops an in-progress negotiation and ignores a late anti-forgery reply', async () => {
    let resolveToken!: (token: { token: string }) => void
    vi.mocked(authenticationAntiforgery).mockReturnValue(new Promise((resolve) => { resolveToken = resolve }))
    signalr.connection.start.mockImplementation(async () => {
      const options = signalr.state.options as { httpClient: { send(request: unknown): Promise<unknown> } }
      await options.httpClient.send({ method: 'POST', url: '/api/v1/realtime/hub/negotiate' })
    })
    const lifecycle = createWorkspaceRealtimeLifecycle(new QueryClient())

    await lifecycle.start()
    await vi.waitFor(() => expect(authenticationAntiforgery).toHaveBeenCalledOnce())
    lifecycle.stop()
    const request = vi.mocked(authenticationAntiforgery).mock.calls[0][0]
    expect(request?.signal?.aborted).toBe(true)
    resolveToken({ token: 'late-token' })
    await vi.waitFor(() => expect(signalr.connection.stop).toHaveBeenCalled())

    expect(setAntiforgeryToken).not.toHaveBeenCalled()
    expect(signalr.connection.invoke).not.toHaveBeenCalled()
  })

  it('clears organization query data while retaining account and platform data on authority loss', async () => {
    const client = new QueryClient()
    client.setQueryData(['projects', 'active'], ['old'])
    client.setQueryData(['access'], ['old'])
    client.setQueryData(['me', 'session'], ['account'])
    client.setQueryData(['account', 'profile'], ['profile'])
    client.setQueryData(['platform', 'tenants'], ['platform'])

    await clearOrganizationQueryState(client)

    expect(client.getQueryData(['projects', 'active'])).toBeUndefined()
    expect(client.getQueryData(['access'])).toBeUndefined()
    expect(client.getQueryData(['me', 'session'])).toEqual(['account'])
    expect(client.getQueryData(['account', 'profile'])).toEqual(['profile'])
    expect(client.getQueryData(['platform', 'tenants'])).toEqual(['platform'])
  })

  it('refreshes access and current workspace after reset while retaining their active query data', async () => {
    const client = new QueryClient()
    const workspaceKey = getWorkspaceCurrentQueryKey()
    client.setQueryData(['projects', 'active'], ['stale-projects'])
    client.setQueryData(['access'], ['stale-access'])
    client.setQueryData(workspaceKey, { organizationId: 'old-org' })

    await clearOrganizationDataForReset(client)

    expect(client.getQueryData(['projects', 'active'])).toBeUndefined()
    expect(client.getQueryData(['access'])).toEqual(['stale-access'])
    expect(client.getQueryData(workspaceKey)).toEqual({ organizationId: 'old-org' })
    expect(client.getQueryState(['access'])?.isInvalidated).toBe(true)
    expect(client.getQueryState(workspaceKey)?.isInvalidated).toBe(true)
  })

  it('does not let late authority-loss cleanup remove a replacement workspace cache', async () => {
    const client = new QueryClient()
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    await lifecycle.start()
    await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalled())
    let finishStop!: () => void
    signalr.connection.stop.mockImplementationOnce(() => new Promise<undefined>((resolve) => { finishStop = () => resolve(undefined) }))
    vi.mocked(realtimeGetHistory).mockRejectedValue(Object.assign(new Error('revoked'), { status: 401 }))
    const push = signalr.connection.on.mock.calls.find(([name]) => name === 'invalidate')![1] as (item: unknown) => void
    push({ eventId: 'wake' })
    await vi.waitFor(() => expect(signalr.connection.stop).toHaveBeenCalled())

    lifecycle.stop()
    client.setQueryData(['projects', 'active'], ['replacement-workspace'])
    finishStop()
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(client.getQueryData(['projects', 'active'])).toEqual(['replacement-workspace'])
  })

  it('refetches mounted organization queries after a workspace reset', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let workspace = 'alpha'
    const observer = new QueryObserver(client, {
      queryKey: ['projects', 'active'], queryFn: async () => [`${workspace}-project`],
    })
    const unsubscribe = observer.subscribe(() => undefined)
    try {
      await vi.waitFor(() => expect(observer.getCurrentResult().data).toEqual(['alpha-project']))
      workspace = 'beta'

      await clearOrganizationDataForReset(client, () => true, false)
      await client.invalidateQueries({ queryKey: ['projects'], refetchType: 'all' })

      expect(observer.getCurrentResult().data).toEqual(['beta-project'])
      expect(client.getQueryData(['projects', 'active'])).toEqual(['beta-project'])
    } finally {
      unsubscribe()
      client.clear()
    }
  })
})
