import type { FeatureAvailabilityState } from '@trykatch/module-sdk'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { featuresCurrent, subscribeFeaturePolicyRejections } from '@trykatch/api-client'
import { useEffect } from 'react'

export const organizationFeatureQueryKey = (organizationId?: string) => ['organization-features', organizationId] as const

export function useOrganizationFeatures(organizationId?: string, authorityReady = true): FeatureAvailabilityState {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: organizationFeatureQueryKey(organizationId),
    queryFn: ({ signal }) => featuresCurrent({ signal }),
    enabled: Boolean(organizationId) && authorityReady,
    retry: false,
    staleTime: 0,
    refetchInterval: 30_000,
    refetchOnWindowFocus: 'always',
  })

  useEffect(() => {
    if (!organizationId || !authorityReady) return
    const key = organizationFeatureQueryKey(organizationId)
    const refresh = () => { void client.invalidateQueries({ queryKey: key, exact: true }) }
    window.addEventListener('focus', refresh)
    const unsubscribe = subscribeFeaturePolicyRejections(({ url }) => {
      // Snapshot failures already settle this query as an error; do not start a retry loop.
      if (url.split('?')[0].endsWith('/api/v1/features')) return
      // Reset clears the old enabled result and cancels its request before a fresh read.
      void client.resetQueries({ queryKey: key, exact: true })
    })
    return () => {
      unsubscribe()
      window.removeEventListener('focus', refresh)
      void client.cancelQueries({ queryKey: key, exact: true })
    }
  }, [client, organizationId, authorityReady])

  const status = !organizationId || !authorityReady || query.isFetching ? 'loading' : query.isError ? 'error' : query.isSuccess ? 'success' : 'loading'
  return { organizationId, snapshot: query.data, status }
}
