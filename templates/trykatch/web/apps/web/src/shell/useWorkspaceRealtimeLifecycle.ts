import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getWorkspaceCurrentQueryKey } from '@trykatch/api-client'
import { clearOrganizationDataForReset, clearOrganizationQueryState, createWorkspaceRealtimeLifecycle } from './realtimeLifecycle'

export function useWorkspaceRealtimeLifecycle({ realtimeContext, hasSession }: { realtimeContext?: string; hasSession: boolean }) {
  const queryClient = useQueryClient()
  const activeRealtimeContext = useRef<string | undefined>(undefined)

  useEffect(() => {
    if (!realtimeContext) {
      let cancelled = false
      if (activeRealtimeContext.current) {
        activeRealtimeContext.current = undefined
        void clearOrganizationQueryState(queryClient, () => !cancelled).then(() => !cancelled && hasSession ? Promise.all([
          queryClient.invalidateQueries({ queryKey: ['me', 'session'] }),
          queryClient.invalidateQueries({ queryKey: ['access'] }),
          queryClient.invalidateQueries({ queryKey: getWorkspaceCurrentQueryKey() }),
        ]) : undefined)
      }
      return () => { cancelled = true }
    }
    const previousContext = activeRealtimeContext.current
    activeRealtimeContext.current = realtimeContext
    const lifecycle = createWorkspaceRealtimeLifecycle(queryClient)
    let cancelled = false
    void (async () => {
      if (previousContext && previousContext !== realtimeContext) {
        await clearOrganizationDataForReset(queryClient, () => !cancelled, false)
        if (cancelled) return
      }
      if (!cancelled) await lifecycle.start()
    })().catch(() => undefined)

    return () => {
      cancelled = true
      lifecycle.stop()
    }
  }, [hasSession, queryClient, realtimeContext])
}
