import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, waitFor } from '@testing-library/react'
import { StrictMode } from 'react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useWorkspaceRealtimeLifecycle } from './useWorkspaceRealtimeLifecycle'

const realtime = vi.hoisted(() => {
  const lifecycles: Array<{ start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn> }> = []
  return {
    lifecycles,
    clearOrganizationDataForReset: vi.fn(async () => undefined),
    clearOrganizationQueryState: vi.fn(async () => undefined),
    createWorkspaceRealtimeLifecycle: vi.fn(() => {
      const lifecycle = { start: vi.fn(async () => undefined), stop: vi.fn() }
      lifecycles.push(lifecycle)
      return lifecycle
    }),
  }
})

vi.mock('./realtimeLifecycle', () => ({
  clearOrganizationDataForReset: realtime.clearOrganizationDataForReset,
  clearOrganizationQueryState: realtime.clearOrganizationQueryState,
  createWorkspaceRealtimeLifecycle: realtime.createWorkspaceRealtimeLifecycle,
}))

function Harness({ context }: { context?: string }) {
  useWorkspaceRealtimeLifecycle({ realtimeContext: context, hasSession: Boolean(context) })
  return null
}

function mount(node: ReactNode) {
  return render(<StrictMode><QueryClientProvider client={new QueryClient()}>{node}</QueryClientProvider></StrictMode>)
}

beforeEach(() => {
  realtime.lifecycles.length = 0
  realtime.clearOrganizationDataForReset.mockClear()
  realtime.clearOrganizationQueryState.mockClear()
  realtime.createWorkspaceRealtimeLifecycle.mockClear()
})

afterEach(cleanup)

describe('workspace realtime hook', () => {
  it('restarts the same context after StrictMode cleanup and stops it on unmount', async () => {
    const view = mount(<Harness context="user-a:org-a:member-a" />)

    await waitFor(() => expect(realtime.lifecycles).toHaveLength(2))
    expect(realtime.lifecycles[0].stop).toHaveBeenCalledOnce()
    expect(realtime.lifecycles[0].start).toHaveBeenCalledOnce()
    expect(realtime.lifecycles[1].start).toHaveBeenCalledOnce()

    view.unmount()
    expect(realtime.lifecycles[1].stop).toHaveBeenCalledOnce()
  })

  it('stops the old lifecycle on workspace change and clears organization data on session loss', async () => {
    const client = new QueryClient()
    const view = render(<QueryClientProvider client={client}><Harness context="user-a:org-a:member-a" /></QueryClientProvider>)
    await waitFor(() => expect(realtime.lifecycles[0]?.start).toHaveBeenCalledOnce())

    view.rerender(<QueryClientProvider client={client}><Harness context="user-a:org-b:member-b" /></QueryClientProvider>)
    await waitFor(() => expect(realtime.lifecycles).toHaveLength(2))
    await waitFor(() => expect(realtime.lifecycles[1].start).toHaveBeenCalledOnce())
    expect(realtime.lifecycles[0].stop).toHaveBeenCalledOnce()
    expect(realtime.clearOrganizationDataForReset).toHaveBeenCalledOnce()

    view.rerender(<QueryClientProvider client={client}><Harness /></QueryClientProvider>)
    await waitFor(() => expect(realtime.clearOrganizationQueryState).toHaveBeenCalledOnce())
    expect(realtime.lifecycles[1].stop).toHaveBeenCalledOnce()
  })
})
