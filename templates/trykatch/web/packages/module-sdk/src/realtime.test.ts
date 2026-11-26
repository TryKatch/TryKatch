import { afterEach, describe, expect, it, vi } from 'vitest'
import { createRealtimeReconciler, type RealtimeHistoryPage, type RealtimeNotification, type RealtimeState } from './realtime'

const state = (cursor: string, generation = 'g1', enabled = true): RealtimeState => ({ cursor, generation, enabled })
const notification = (eventId: string): RealtimeNotification => ({
  moduleId: 'projects', eventKey: 'projects.created', schemaVersion: 1, eventId,
  audience: { policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 }, kind: 0 },
})
const page = (cursor: string, items: RealtimeNotification[], hasMore = false, generation = 'g1'): RealtimeHistoryPage => ({ cursor, generation, items, hasMore })

afterEach(() => vi.useRealTimers())

describe('createRealtimeReconciler', () => {
  it('refetches all owned data before adopting the state head cursor', async () => {
    const order: string[] = []
    const reconciler = createRealtimeReconciler({
      fetchState: async () => { order.push('state'); return state('head') },
      fetchHistory: async () => { order.push('history'); return page('next', []) },
      refetchAll: async (isCurrent) => { expect(isCurrent()).toBe(true); order.push('refetch') },
      apply: async () => { order.push('apply') },
      repairIntervalMs: 60_000,
    })

    await reconciler.start()
    await reconciler.wake()

    expect(order).toEqual(['state', 'refetch', 'history'])
    reconciler.stop()
  })

  it('processes pages sequentially and advances the cursor only after every item succeeds', async () => {
    const cursors: Array<string | null> = []
    const applied: string[] = []
    let fail = true
    const reconciler = createRealtimeReconciler({
      fetchState: async () => state('head'),
      fetchHistory: async (cursor) => {
        cursors.push(cursor)
        return page('page-1', [notification('one'), notification('two')])
      },
      refetchAll: async () => undefined,
      apply: async (item) => { applied.push(item.eventId); if (item.eventId === 'two' && fail) throw new Error('transient') },
      repairIntervalMs: 60_000,
    })

    await reconciler.start()
    await expect(reconciler.wake()).rejects.toThrow('transient')
    fail = false
    await reconciler.wake()

    expect(cursors).toEqual(['head', 'head'])
    expect(applied).toEqual(['one', 'two', 'two'])
    reconciler.stop()
  })

  it('coalesces concurrent duplicate and out-of-order wakeups into serialized history reads', async () => {
    let release!: () => void
    let active = 0
    let maximumActive = 0
    let pages = 0
    const reconciler = createRealtimeReconciler({
      fetchState: async () => state('head'),
      fetchHistory: async () => {
        active++
        maximumActive = Math.max(maximumActive, active)
        pages++
        await new Promise<void>((resolve) => { release = resolve })
        active--
        return page(`page-${pages}`, [])
      },
      refetchAll: async () => undefined,
      apply: async () => undefined,
      repairIntervalMs: 60_000,
    })

    await reconciler.start()
    const first = reconciler.wake()
    await vi.waitFor(() => expect(pages).toBe(1))
    const second = reconciler.wake()
    const third = reconciler.wake()
    release()
    await vi.waitFor(() => expect(pages).toBe(2))
    release()
    await Promise.all([first, second, third])

    expect(maximumActive).toBe(1)
    expect(pages).toBe(2)
    reconciler.stop()
  })

  it('resets to authoritative state and refetches all data on a reset response or generation mismatch', async () => {
    const refetches: string[] = []
    let states = 0
    let history = 0
    const resetError = Object.assign(new Error('reset'), { status: 409, code: 'realtime_reset_required' })
    const reconciler = createRealtimeReconciler({
      fetchState: async () => state(`head-${++states}`, `g${states}`),
      fetchHistory: async () => {
        history++
        if (history === 1) throw resetError
        return page('page-2', [notification('ignored')], false, 'wrong-generation')
      },
      refetchAll: async () => { refetches.push(`full-${states}`) },
      apply: async () => { throw new Error('old generation item must be discarded') },
      repairIntervalMs: 60_000,
    })

    await reconciler.start()
    await reconciler.wake()
    await reconciler.wake()

    expect(states).toBe(3)
    expect(refetches).toEqual(['full-1', 'full-2', 'full-3'])
    reconciler.stop()
  })

  it('keeps deduplication bounded and does not record failed invalidations', async () => {
    const processed: string[] = []
    const reconciler = createRealtimeReconciler({
      fetchState: async () => state('head'),
      fetchHistory: async () => page('next', [notification('a'), notification('a'), notification('b'), notification('c')]),
      refetchAll: async () => undefined,
      apply: async (item) => { processed.push(item.eventId) },
      dedupeLimit: 2,
      repairIntervalMs: 60_000,
    })

    await reconciler.start()
    await reconciler.wake()
    expect(processed).toEqual(['a', 'b', 'c'])
    reconciler.stop()
  })

  it('repairs periodically even when live delivery is disabled', async () => {
    vi.useFakeTimers()
    const fetchHistory = vi.fn(async () => page('next', []))
    const reconciler = createRealtimeReconciler({
      fetchState: async () => state('head', 'g1', false),
      fetchHistory,
      refetchAll: async () => undefined,
      apply: async () => undefined,
      repairIntervalMs: 1_000,
    })

    await reconciler.start()
    await reconciler.wake()
    expect(fetchHistory).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1_000)
    await vi.waitFor(() => expect(fetchHistory).toHaveBeenCalledTimes(2))
    reconciler.stop()
  })

  it('ignores late state and history replies after stop', async () => {
    let resolveState!: (value: RealtimeState) => void
    const refetchAll = vi.fn()
    const reconciler = createRealtimeReconciler({
      fetchState: async () => new Promise<RealtimeState>((resolve) => { resolveState = resolve }),
      fetchHistory: async () => page('next', []),
      refetchAll,
      apply: async () => undefined,
      repairIntervalMs: 60_000,
    })

    const starting = reconciler.start()
    reconciler.stop()
    resolveState(state('head'))
    await starting

    expect(refetchAll).not.toHaveBeenCalled()
  })

  it('supports nullable opaque history cursors without synthesizing a starting position', async () => {
    const cursors: Array<string | null> = []
    const reconciler = createRealtimeReconciler({
      fetchState: async () => { throw new Error('state should not be used') },
      fetchHistory: async (cursor) => { cursors.push(cursor); return page('opaque/next', []) },
      refetchAll: async () => undefined,
      apply: async () => undefined,
      repairIntervalMs: 60_000,
      initialCursor: null,
      initialGeneration: 'g1',
    })

    await reconciler.start()
    await reconciler.wake()
    expect(cursors).toEqual([null])
    reconciler.stop()
  })
})
