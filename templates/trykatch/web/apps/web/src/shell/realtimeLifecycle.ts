import { DefaultHttpClient, HubConnection, HubConnectionBuilder, HttpRequest, HttpTransportType, LogLevel } from '@microsoft/signalr'
import { QueryClient, QueryKey } from '@tanstack/react-query'
import { authenticationAntiforgery, getWorkspaceCurrentQueryKey, realtimeGetHistory, realtimeGetState, setAntiforgeryToken } from '@trykatch/api-client'
import { createRealtimeReconciler, type RealtimeNotification, type RealtimeState } from '@trykatch/module-sdk'
import { workspaceModules } from '../modules'

const organizationQueryRoots = new Set(['me', 'account', 'platform'])

function isOrganizationQuery(queryKey: QueryKey) {
  const root = queryKey[0]
  return typeof root !== 'string' || !organizationQueryRoots.has(root)
}

export async function clearOrganizationQueryState(queryClient: QueryClient, isCurrent: () => boolean = () => true) {
  const predicate = ({ queryKey }: { queryKey: QueryKey }) => isOrganizationQuery(queryKey)
  if (!isCurrent()) return
  await queryClient.cancelQueries({ predicate })
  if (!isCurrent()) return
  // Reset notifies mounted observers; deleting their cache entries leaves stale results attached.
  await queryClient.resetQueries({ predicate })
}

export async function clearOrganizationDataForReset(queryClient: QueryClient, isCurrent: () => boolean = () => true, refreshWorkspace = true) {
  const predicate = ({ queryKey }: { queryKey: QueryKey }) => isOrganizationQuery(queryKey)
    && queryKey[0] !== 'access' && queryKey[0] !== '/api/v1/workspace/current'
  if (!isCurrent()) return
  await queryClient.cancelQueries({ predicate })
  if (!isCurrent()) return
  // Preserve mounted observers so their owned queries can refetch in the new workspace.
  // Removing active queries leaves those observers attached to detached, stale query objects.
  await queryClient.resetQueries({ predicate })
  if (!isCurrent()) return
  await queryClient.invalidateQueries({ queryKey: ['access'] })
  if (refreshWorkspace && isCurrent()) await queryClient.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey() })
}

function errorStatus(error: unknown) {
  if (!error || typeof error !== 'object') return undefined
  const candidate = error as { status?: number; statusCode?: number }
  return candidate.status ?? candidate.statusCode
}

class AntiforgerySignalRHttpClient extends DefaultHttpClient {
  constructor(private readonly getToken: () => Promise<string>) { super({ log: () => undefined }) }

  async send(request: HttpRequest) {
    if ((request.method ?? 'GET').toUpperCase() !== 'POST') return super.send(request)
    const token = await this.getToken()
    return super.send({ ...request, headers: { ...request.headers, 'X-CSRF-TOKEN': token } })
  }
}

export function createWorkspaceRealtimeLifecycle(queryClient: QueryClient) {
  let active = true
  let disposed = false
  const lifetime = new AbortController()
  let connection: HubConnection | undefined
  let connectionStarting = false
  let liveEnabled = false
  let reconnectTimer: ReturnType<typeof setTimeout> | undefined
  let reconnectDelay = 1_000
  let authorityLossHandled = false
  let reconciler: ReturnType<typeof createRealtimeReconciler>

  const isCurrent = () => active

  async function getAntiforgeryToken() {
    let response
    try {
      response = await authenticationAntiforgery({ signal: lifetime.signal })
    } catch (error) {
      handleAuthorityError(error)
      throw error
    }
    if (!isCurrent()) throw new DOMException('Realtime lifecycle stopped', 'AbortError')
    setAntiforgeryToken(response.token)
    return response.token
  }

  async function loseAuthority() {
    if (!isCurrent() || authorityLossHandled) return
    authorityLossHandled = true
    active = false
    lifetime.abort()
    liveEnabled = false
    reconciler.stop()
    if (reconnectTimer !== undefined) clearTimeout(reconnectTimer)
    reconnectTimer = undefined
    await connection?.stop().catch(() => undefined)
    connection = undefined
    if (disposed) return
    await clearOrganizationQueryState(queryClient, () => !disposed)
    if (disposed) return
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['me', 'session'] }),
      queryClient.invalidateQueries({ queryKey: ['access'] }),
      queryClient.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey() }),
    ])
  }

  function handleAuthorityError(error: unknown) {
    const status = errorStatus(error)
    const problem = error && typeof error === 'object' ? (error as { problem?: { title?: unknown } }).problem : undefined
    // Organization middleware can reject a cleared workspace before the realtime reader runs.
    if (status === 401 || status === 403 || status === 409 && problem?.title === 'Workspace context required') void loseAuthority()
  }

  async function invalidateKeys(keys: readonly (readonly unknown[])[], guard: () => boolean) {
    const unique = new Map(keys.map((key) => [JSON.stringify(key), key]))
    for (const key of unique.values()) {
      if (!guard()) return
      await queryClient.invalidateQueries({ queryKey: [...key], refetchType: 'none' })
      if (!guard()) return
      const queries = queryClient.getQueryCache().findAll({ queryKey: [...key] })
      for (const query of queries) {
        if (!guard()) return
        try {
          await queryClient.refetchQueries({ queryKey: query.queryKey, exact: true, type: 'all' }, { throwOnError: true })
        } catch (error) {
          if (errorStatus(error) !== 403) throw error
          if (!guard()) return
          // A domain-specific denial clears only stale result data. Keep the settled
          // error and fetch history visible while allowing a later event to retry it.
          if (queryClient.getQueryCache().find({ queryKey: query.queryKey, exact: true }) === query) {
            query.setState({ data: undefined, dataUpdatedAt: 0, isInvalidated: true })
          }
        }
      }
    }
  }

  async function refetchAll(guard: () => boolean) {
    const keys = workspaceModules.realtimeInvalidations.flatMap((item) => item.queryKeys)
    await invalidateKeys(keys, guard)
  }

  async function apply(notification: RealtimeNotification, guard: () => boolean) {
    for (const contribution of workspaceModules.realtimeInvalidations) {
      if (!contribution.matches(notification)) continue
      await invalidateKeys(contribution.queryKeys, guard)
      if (!guard()) return
    }
  }

  function scheduleReconnect() {
    if (!isCurrent() || !liveEnabled || reconnectTimer !== undefined) return
    reconnectTimer = setTimeout(() => {
      reconnectTimer = undefined
      void connect().catch(handleAuthorityError)
    }, reconnectDelay)
    reconnectDelay = Math.min(reconnectDelay * 2, 30_000)
  }

  async function replaceSubscriptions(candidate: HubConnection) {
    if (!isCurrent()) return
    await candidate.invoke('ReplaceSubscriptions', [...workspaceModules.realtimeSubscriptions])
  }

  async function connect() {
    if (!isCurrent() || !liveEnabled || connection || connectionStarting) return
    connectionStarting = true
    const candidate = new HubConnectionBuilder()
      .withUrl('/api/v1/realtime/hub', {
        transport: HttpTransportType.WebSockets,
        withCredentials: true,
        httpClient: new AntiforgerySignalRHttpClient(getAntiforgeryToken),
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build()
    candidate.on('invalidate', (notification: RealtimeNotification) => {
      if (!isCurrent() || !notification) return
      void reconciler.wake().catch(() => undefined)
    })
    candidate.onreconnected(() => {
      void replaceSubscriptions(candidate).then(() => reconciler.wake().catch(() => undefined)).catch(handleAuthorityError)
    })
    candidate.onclose(() => {
      if (connection === candidate) connection = undefined
      if (!isCurrent() || !liveEnabled) return
      void reconciler.wake().catch(() => undefined)
      scheduleReconnect()
    })
    connection = candidate
    try {
      await candidate.start()
      if (!isCurrent() || !liveEnabled) { await candidate.stop(); if (connection === candidate) connection = undefined; return }
      await replaceSubscriptions(candidate)
      if (!isCurrent() || !liveEnabled) { await candidate.stop(); if (connection === candidate) connection = undefined; return }
      connection = candidate
      reconnectDelay = 1_000
      void reconciler.wake().catch(() => undefined)
    } catch (error) {
      if (connection === candidate) connection = undefined
      await candidate.stop().catch(() => undefined)
      handleAuthorityError(error)
      if (isCurrent()) scheduleReconnect()
    } finally {
      connectionStarting = false
      if (isCurrent() && liveEnabled && !connection) scheduleReconnect()
    }
  }

  reconciler = createRealtimeReconciler({
    fetchState: async (signal) => {
      try { return await realtimeGetState({ signal }) }
      catch (error) { handleAuthorityError(error); throw error }
    },
    fetchHistory: async (cursor, signal) => {
      try { return await realtimeGetHistory(cursor === null ? undefined : { cursor }, { signal }) }
      catch (error) { handleAuthorityError(error); throw error }
    },
    refetchAll,
    apply,
    onReset: async (_state, _previousGeneration, guard) => {
      liveEnabled = false
      const current = connection
      connection = undefined
      if (current) await current.stop().catch(() => undefined)
      if (guard()) await clearOrganizationDataForReset(queryClient, guard)
    },
    onState: (state: RealtimeState, guard) => {
      if (!guard()) return
      liveEnabled = state.enabled
      if (state.enabled) void connect().catch(handleAuthorityError)
      else {
        if (reconnectTimer !== undefined) clearTimeout(reconnectTimer)
        reconnectTimer = undefined
        const current = connection
        connection = undefined
        if (current) void current.stop().catch(() => undefined)
      }
    },
  })

  return {
    async start() { await reconciler.start() },
    stop() {
      if (disposed) return
      disposed = true
      active = false
      lifetime.abort()
      liveEnabled = false
      reconciler.stop()
      if (reconnectTimer !== undefined) clearTimeout(reconnectTimer)
      reconnectTimer = undefined
      const current = connection
      connection = undefined
      if (current) void current.stop().catch(() => undefined)
    },
    wake() { return reconciler.wake() },
  }
}
