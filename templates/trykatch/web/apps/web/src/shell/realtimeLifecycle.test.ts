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

  it.each([
    ['membership revoked', 403, 'Workspace access denied'],
    ['workspace cookie cleared', 409, 'Workspace context required'],
  ])('clears mounted organization results when %s while the account session remains valid', async (_case, status, title) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const keys = [['projects', 'active'], ['access'], getWorkspaceCurrentQueryKey()]
    const denied = Object.assign(new Error(title), { status, problem: { title } })
    let revoked = false
    const observers = keys.map((queryKey) => {
      client.setQueryData(queryKey, ['previous-workspace'])
      return new QueryObserver(client, {
        queryKey, staleTime: Infinity,
        queryFn: async () => { if (revoked) throw denied; return ['previous-workspace'] },
      })
    })
    const unsubscribe = observers.map((observer) => observer.subscribe(() => undefined))
    client.setQueryData(['me', 'session'], { userId: 'still-signed-in' })
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    try {
      await lifecycle.start()
      await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalled())
      await lifecycle.wake()
      revoked = true
      vi.mocked(realtimeGetHistory).mockRejectedValue(denied)
      const push = signalr.connection.on.mock.calls.find(([name]) => name === 'invalidate')![1] as (item: unknown) => void
      push({ eventId: 'wake' })

      await vi.waitFor(() => expect(signalr.connection.stop).toHaveBeenCalled())
      await vi.waitFor(() => expect(observers.map((observer) => observer.getCurrentResult().data)).toEqual([undefined, undefined, undefined]))
      expect(client.getQueryData(['me', 'session'])).toEqual({ userId: 'still-signed-in' })
    } finally {
      lifecycle.stop()
      unsubscribe.forEach((stop) => stop())
      client.clear()
    }
  })

  it('retains a failed invalidation continuation and retries the real domain query after recovery', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let fail = false
    let domainValue = 'before'
    const observer = new QueryObserver(client, {
      queryKey: ['projects', 'active'], staleTime: Infinity,
      queryFn: async () => { if (fail) throw Object.assign(new Error('unavailable'), { status: 500 }); return [domainValue] },
    })
    const unsubscribe = observer.subscribe(() => undefined)
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    try {
      await lifecycle.start()
      await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalled())
      await lifecycle.wake()
      const event = {
        moduleId: 'projects', eventKey: 'projects.created', schemaVersion: 1, eventId: 'event-id',
        audience: { kind: 0, policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 } },
      }
      vi.mocked(realtimeGetHistory).mockClear().mockResolvedValue({
        items: [event], generation: 'server-generation', cursor: 'after-event', hasMore: false,
      })
      fail = true
      await expect(lifecycle.wake()).rejects.toMatchObject({ status: 500 })
      expect(observer.getCurrentResult().data).toEqual(['before'])

      fail = false
      domainValue = 'recovered'
      await lifecycle.wake()
      expect(vi.mocked(realtimeGetHistory).mock.calls.slice(0, 2).map(([params]) => params?.cursor)).toEqual(['next', 'next'])
      expect(observer.getCurrentResult().data).toEqual(['recovered'])
      await lifecycle.wake()
      expect(vi.mocked(realtimeGetHistory).mock.calls[2][0]?.cursor).toBe('after-event')
    } finally {
      lifecycle.stop()
      unsubscribe()
      client.clear()
    }
  })

  it('retries bootstrap before adopting its head when an owned query refetch fails', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let fail = true
    const observer = new QueryObserver(client, {
      queryKey: ['projects', 'active'], staleTime: Infinity,
      queryFn: async () => { if (fail) throw Object.assign(new Error('unavailable'), { status: 500 }); return ['recovered'] },
    })
    client.setQueryData(['projects', 'active'], ['before'])
    const unsubscribe = observer.subscribe(() => undefined)
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    try {
      await expect(lifecycle.start()).rejects.toMatchObject({ status: 500 })
      expect(signalr.connection.start).not.toHaveBeenCalled()
      fail = false
      await lifecycle.wake()
      expect(realtimeGetState).toHaveBeenCalledTimes(2)
      expect(observer.getCurrentResult().data).toEqual(['recovered'])
    } finally {
      lifecycle.stop()
      unsubscribe()
      client.clear()
    }
  })

  it('repairs rate-limited bootstrap through the periodic timer before subscribing', async () => {
    vi.useFakeTimers()
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let rateLimited = true
    const observer = new QueryObserver(client, {
      queryKey: ['projects', 'active'], staleTime: Infinity,
      queryFn: async () => { if (rateLimited) throw Object.assign(new Error('rate limited'), { status: 429 }); return ['recovered'] },
    })
    client.setQueryData(['projects', 'active'], ['before'])
    const unsubscribe = observer.subscribe(() => undefined)
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    try {
      await expect(lifecycle.start()).rejects.toMatchObject({ status: 429 })
      expect(signalr.connection.start).not.toHaveBeenCalled()

      rateLimited = false
      await vi.advanceTimersByTimeAsync(15_000)

      expect(realtimeGetState).toHaveBeenCalledTimes(2)
      expect(observer.getCurrentResult().data).toEqual(['recovered'])
      expect(signalr.connection.invoke).toHaveBeenCalledWith('ReplaceSubscriptions', expect.any(Array))
    } finally {
      lifecycle.stop()
      unsubscribe()
      client.clear()
      vi.useRealTimers()
    }
  })

  it('settles an inactive retained domain 403 during bootstrap and retries it after recovery', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let fail = false
    const queryKey = ['projects', 'retained']
    const observer = new QueryObserver(client, {
      queryKey, staleTime: Infinity,
      queryFn: async () => { if (fail) throw Object.assign(new Error('forbidden'), { status: 403 }); return ['recovered'] },
    })
    const unsubscribe = observer.subscribe(() => undefined)
    await vi.waitFor(() => expect(observer.getCurrentResult().data).toEqual(['recovered']))
    unsubscribe()
    fail = true
    client.setQueryData(queryKey, ['stale-domain-data'])
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    try {
      await lifecycle.start()

      expect(client.getQueryData(queryKey)).toBeUndefined()
      expect(signalr.connection.invoke).toHaveBeenCalledWith('ReplaceSubscriptions', expect.any(Array))
      expect(signalr.connection.stop).not.toHaveBeenCalled()

      await lifecycle.wake()
      const recoveryEvent = {
        moduleId: 'projects', eventKey: 'projects.created', schemaVersion: 1, eventId: 'recovery-event',
        audience: { kind: 0, policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 } },
      }
      fail = false
      vi.mocked(realtimeGetHistory).mockResolvedValue({
        items: [recoveryEvent], generation: 'server-generation', cursor: 'after-recovery-event', hasMore: false,
      })
      await lifecycle.wake()
      expect(client.getQueryData(queryKey)).toEqual(['recovered'])
    } finally {
      lifecycle.stop()
      client.clear()
    }
  })

  it('clears a domain 403 during notification application without losing realtime authority', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let fail = false
    const queryKey = ['projects', 'active']
    const observer = new QueryObserver(client, {
      queryKey, staleTime: Infinity,
      queryFn: async () => { if (fail) throw Object.assign(new Error('forbidden'), { status: 403 }); return ['recovered'] },
    })
    const unsubscribe = observer.subscribe(() => undefined)
    await vi.waitFor(() => expect(observer.getCurrentResult().data).toEqual(['recovered']))
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    const event = {
      moduleId: 'projects', eventKey: 'projects.created', schemaVersion: 1, eventId: 'denied-event',
      audience: { kind: 0, policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 } },
    }
    try {
      await lifecycle.start()
      await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalled())
      vi.mocked(realtimeGetHistory).mockClear().mockResolvedValue({
        items: [event], generation: 'server-generation', cursor: 'after-denied-event', hasMore: false,
      })
      fail = true

      await expect(lifecycle.wake()).resolves.toBeUndefined()

      expect(observer.getCurrentResult().data).toBeUndefined()
      expect(observer.getCurrentResult()).toMatchObject({ isError: true, isLoading: false })
      expect(observer.getCurrentResult().error).toMatchObject({ status: 403 })
      expect(signalr.connection.stop).not.toHaveBeenCalled()
      expect(vi.mocked(realtimeGetHistory).mock.calls[0][0]?.cursor).toBe('next')
      vi.mocked(realtimeGetHistory).mockResolvedValue({ items: [], generation: 'server-generation', cursor: 'after-denied-event', hasMore: false })
      await lifecycle.wake()
      expect(vi.mocked(realtimeGetHistory).mock.calls[1][0]?.cursor).toBe('after-denied-event')
    } finally {
      lifecycle.stop()
      unsubscribe()
      client.clear()
    }
  })

  it('does not let a domain 403 hide a sibling transient failure or advance its history cursor', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let denied = false
    let unavailable = false
    const deniedKey = ['projects', 'denied']
    const transientKey = ['projects', 'transient']
    const observers = [
      new QueryObserver(client, { queryKey: deniedKey, staleTime: Infinity, queryFn: async () => { if (denied) throw Object.assign(new Error('forbidden'), { status: 403 }); return ['denied-data'] } }),
      new QueryObserver(client, { queryKey: transientKey, staleTime: Infinity, queryFn: async () => { if (unavailable) throw Object.assign(new Error('unavailable'), { status: 500 }); return ['transient-data'] } }),
    ]
    const unsubscribe = observers.map((observer) => observer.subscribe(() => undefined))
    await vi.waitFor(() => expect(observers.map((observer) => observer.getCurrentResult().data)).toEqual([['denied-data'], ['transient-data']]))
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    const event = {
      moduleId: 'projects', eventKey: 'projects.created', schemaVersion: 1, eventId: 'mixed-failure-event',
      audience: { kind: 0, policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 } },
    }
    try {
      await lifecycle.start()
      await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalled())
      vi.mocked(realtimeGetHistory).mockClear().mockResolvedValue({
        items: [event], generation: 'server-generation', cursor: 'after-mixed-event', hasMore: false,
      })
      denied = true
      unavailable = true

      await expect(lifecycle.wake()).rejects.toMatchObject({ status: 500 })

      expect(observers[0].getCurrentResult().data).toBeUndefined()
      expect(observers[1].getCurrentResult().data).toEqual(['transient-data'])
      expect(vi.mocked(realtimeGetHistory).mock.calls[0][0]?.cursor).toBe('next')
      expect(signalr.connection.stop).not.toHaveBeenCalled()
    } finally {
      lifecycle.stop()
      unsubscribe.forEach((stop) => stop())
      client.clear()
    }
  })

  it('does not interpret an unrelated conflict as lost workspace authority', async () => {
    const client = new QueryClient()
    const lifecycle = createWorkspaceRealtimeLifecycle(client)
    try {
      await lifecycle.start()
      await vi.waitFor(() => expect(signalr.connection.invoke).toHaveBeenCalled())
      await lifecycle.wake()
      client.setQueryData(['projects', 'active'], ['still-current'])
      vi.mocked(realtimeGetHistory).mockRejectedValue(Object.assign(new Error('conflict'), {
        status: 409, problem: { title: 'Unrelated conflict' },
      }))
      const push = signalr.connection.on.mock.calls.find(([name]) => name === 'invalidate')![1] as (item: unknown) => void
      push({ eventId: 'wake' })
      await new Promise((resolve) => setTimeout(resolve, 0))
      expect(signalr.connection.stop).not.toHaveBeenCalled()
      expect(client.getQueryData(['projects', 'active'])).toEqual(['still-current'])
    } finally {
      lifecycle.stop()
      client.clear()
    }
  })
})
