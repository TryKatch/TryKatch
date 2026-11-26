export interface RealtimePolicyRef {
  ownerModuleId: string
  policyKey: string
  policyVersion: number | string
}

export interface RealtimeResourceRef {
  resourceKey: string
  resourceId: string
}

export interface RealtimeAudience {
  policy: RealtimePolicyRef
  /** The wire contract uses the server enum's numeric values. */
  kind: number
  userId?: string | null
  resource?: RealtimeResourceRef | null
}

export interface RealtimeNotification {
  moduleId: string
  eventKey: string
  schemaVersion: number | string
  eventId: string
  audience: RealtimeAudience
}

export interface RealtimeState {
  enabled: boolean
  generation: string
  cursor: string
}

export interface RealtimeHistoryPage {
  items: readonly RealtimeNotification[]
  generation: string
  cursor: string
  hasMore: boolean
}

export interface WebRealtimeInvalidationContribution {
  id: string
  subscription: RealtimeAudience
  matches(notification: RealtimeNotification): boolean
  queryKeys: readonly (readonly unknown[])[]
}

export interface RealtimeReconcilerOptions {
  fetchState(signal: AbortSignal): Promise<RealtimeState>
  fetchHistory(cursor: string | null, signal: AbortSignal): Promise<RealtimeHistoryPage>
  /** Reconcile every query owned by installed realtime contributions at the state head. */
  refetchAll(isCurrent: () => boolean): Promise<void>
  /** Apply one matching notification; check isCurrent before each cache mutation. */
  apply(notification: RealtimeNotification, isCurrent: () => boolean): Promise<void>
  onReset?(state: RealtimeState, previousGeneration: string | undefined, isCurrent: () => boolean): Promise<void>
  onState?(state: RealtimeState, isCurrent: () => boolean): void
  onError?(error: unknown): void
  repairIntervalMs?: number
  dedupeLimit?: number
  initialCursor?: string | null
  initialGeneration?: string
}

/** Owns only HTTP reconciliation. Transport callbacks should call wake(); pushes never move its cursor. */
export function createRealtimeReconciler(options: RealtimeReconcilerOptions) {
  let running = false
  let epoch = 0
  let abortController: AbortController | undefined
  let repairTimer: ReturnType<typeof setInterval> | undefined
  let cursor = options.initialCursor ?? null
  let generation = options.initialGeneration
  let ready = options.initialGeneration !== undefined
  let hasBootstrapped = options.initialGeneration !== undefined
  let bootstrapPromise: Promise<void> | undefined
  let drainPromise: Promise<void> | undefined
  let wakeRequested = false
  const seen = new Map<string, true>()
  const configuredDedupeLimit = options.dedupeLimit ?? 512
  const dedupeLimit = Number.isFinite(configuredDedupeLimit)
    ? Math.min(512, Math.max(1, Math.trunc(configuredDedupeLimit))) : 512
  const configuredRepairInterval = options.repairIntervalMs ?? 15_000
  const repairIntervalMs = Number.isFinite(configuredRepairInterval)
    ? Math.min(60_000, Math.max(1_000, Math.trunc(configuredRepairInterval))) : 15_000

  const isCurrent = (run: number) => running && epoch === run && !abortController?.signal.aborted

  async function bootstrap(run: number) {
    if (ready) return
    if (bootstrapPromise) return bootstrapPromise
    const signal = abortController?.signal
    if (!signal) return
    const operation = (async () => {
      const current = await options.fetchState(signal)
      if (!isCurrent(run)) return
      if (hasBootstrapped) {
        await options.onReset?.(current, generation, () => isCurrent(run))
        if (!isCurrent(run)) return
      }
      await options.refetchAll(() => isCurrent(run))
      if (!isCurrent(run)) return
      generation = current.generation
      cursor = current.cursor
      ready = true
      hasBootstrapped = true
      options.onState?.(current, () => isCurrent(run))
    })()
    bootstrapPromise = operation
    try { await operation } finally { if (bootstrapPromise === operation) bootstrapPromise = undefined }
  }

  function remember(id: string) {
    seen.delete(id)
    seen.set(id, true)
    while (seen.size > dedupeLimit) seen.delete(seen.keys().next().value!)
  }

  function eventIdentity(item: RealtimeNotification) {
    return `${item.moduleId}\0${item.eventKey}\0${item.schemaVersion}\0${item.eventId}`
  }

  function isResetError(error: unknown) {
    if (!error || typeof error !== 'object') return false
    const candidate = error as { status?: unknown; code?: unknown; type?: unknown; problem?: { title?: unknown; type?: unknown } }
    return candidate.status === 409 && (candidate.code === 'realtime_reset_required' || candidate.type === 'realtime_reset_required'
      || candidate.problem?.title === 'realtime_reset_required' || candidate.problem?.type === 'realtime_reset_required')
  }

  async function drainPages(run: number) {
    while (isCurrent(run) && ready) {
      const signal = abortController?.signal
      if (!signal) return
      let page: RealtimeHistoryPage
      try {
        page = await options.fetchHistory(cursor, signal)
      } catch (error) {
        if (isResetError(error)) {
          ready = false
          seen.clear()
          await bootstrap(run)
          return
        }
        throw error
      }
      if (!isCurrent(run)) return
      if (page.generation !== generation) {
        ready = false
        seen.clear()
        await bootstrap(run)
        return
      }

      for (const item of page.items) {
        if (!isCurrent(run)) return
        const identity = eventIdentity(item)
        if (seen.has(identity)) continue
        await options.apply(item, () => isCurrent(run))
        if (!isCurrent(run)) return
        remember(identity)
      }

      if (!isCurrent(run)) return
      if (page.hasMore && page.cursor === cursor) throw new Error('Realtime history page did not advance its opaque cursor.')
      // A page is committed only after every awaited item application has succeeded.
      cursor = page.cursor
      if (!page.hasMore) return
    }
  }

  async function drain(run: number) {
    do {
      wakeRequested = false
      await bootstrap(run)
      if (isCurrent(run) && ready) await drainPages(run)
    } while (wakeRequested && isCurrent(run))
  }

  function wake() {
    if (!running) return Promise.resolve()
    wakeRequested = true
    if (drainPromise) return drainPromise
    const run = epoch
    const operation = drain(run)
    drainPromise = operation
    return operation.finally(() => { if (drainPromise === operation) drainPromise = undefined })
  }

  return {
    async start() {
      if (running) return bootstrapPromise ?? Promise.resolve()
      running = true
      const run = ++epoch
      abortController = new AbortController()
      repairTimer = setInterval(() => { void wake().catch((error) => options.onError?.(error)) }, repairIntervalMs)
      try { await bootstrap(run) } catch (error) { options.onError?.(error); throw error }
    },
    wake,
    stop() {
      if (!running) return
      running = false
      epoch++
      abortController?.abort()
      abortController = undefined
      if (repairTimer !== undefined) clearInterval(repairTimer)
      repairTimer = undefined
      ready = options.initialGeneration !== undefined
      hasBootstrapped = options.initialGeneration !== undefined
      cursor = options.initialCursor ?? null
      generation = options.initialGeneration
      wakeRequested = false
    },
    get cursor() { return cursor },
    get generation() { return generation },
  }
}
