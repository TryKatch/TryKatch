import { Fragment, type ReactNode } from 'react'
import { useModuleFeatures, type WebRoute } from '@trykatch/module-sdk'
import { useQuery } from '@tanstack/react-query'
import { getWorkspaceCurrentQueryKey, workspaceCurrent } from '@trykatch/api-client'
import { EmptyState, Skeleton } from '@trykatch/ui'
import { useI18n } from '../../i18n/I18nProvider'

export function ModuleRouteBoundary({ route, children }: { route: Pick<WebRoute, 'requiredPermission' | 'requiredFeature'>; children: ReactNode }) {
  const { t } = useI18n()
  const features = useModuleFeatures()
  const workspace = useQuery({
    queryKey: getWorkspaceCurrentQueryKey(),
    queryFn: ({ signal }) => workspaceCurrent({ signal }),
    enabled: Boolean(route.requiredPermission && features.organizationId),
    retry: false,
  })
  if (route.requiredFeature && features.status === 'loading'
    || route.requiredPermission && (workspace.isPending || workspace.isFetching)) return <div role="status" aria-label={t('Checking feature availability')}><Skeleton /></div>
  if (route.requiredPermission && (!workspace.isSuccess || workspace.data.organizationId !== features.organizationId
    || !workspace.data.permissions.includes(route.requiredPermission)))
    return <EmptyState title={t('Access unavailable')} description={t('You do not have permission to view this page.')} />
  if (route.requiredFeature && !features.isEnabled(route.requiredFeature))
    return <EmptyState title={t('Feature unavailable')} description={t(features.status === 'error'
      ? 'Feature availability could not be checked. Try again shortly.' : 'This feature is unavailable in this organization.')} />
  return <Fragment key={features.organizationId}>{children}</Fragment>
}
