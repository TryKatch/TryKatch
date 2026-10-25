import { useQuery } from '@tanstack/react-query'
import { quotasCurrentUsage, type OrganizationQuotaUsage, type OrganizationQuotaUsageResource } from '@trykatch/api-client'
import { Badge, Button, Skeleton, Surface } from '@trykatch/ui'
import { useI18n } from '../i18n/I18nProvider'

type UsageResource = OrganizationQuotaUsageResource

function formatQuantity(value: bigint, unit: string, locale: string, t: (message: string) => string) {
  const formatted = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(value)
  return `${formatted} ${t(unit === 'Bytes' ? 'bytes' : 'items')}`
}

function formatNullableQuantity(value: string | null, unit: string, locale: string, t: (message: string) => string) {
  return value === null ? null : formatQuantity(BigInt(value), unit, locale, t)
}

function usageSummary(resource: UsageResource, locale: string, t: (message: string) => string) {
  const committed = resource.committed === null ? null : BigInt(resource.committed)
  const reserved = resource.reserved === null ? null : BigInt(resource.reserved)
  const held = resource.reconciliationHeld === null ? null : BigInt(resource.reconciliationHeld)
  const limit = resource.limit === null ? null : BigInt(resource.limit)
  const complete = committed !== null && reserved !== null && held !== null

  if (resource.mode === 'Unlimited') return { total: complete ? formatQuantity(committed! + reserved! + held!, resource.unit, locale, t) : null, remaining: null, over: null }
  if (resource.mode !== 'Finite' || limit === null || !complete) return { total: null, remaining: null, over: null }

  const total = committed! + reserved! + held!
  return {
    total: formatQuantity(total, resource.unit, locale, t),
    remaining: formatQuantity(total < limit ? limit - total : 0n, resource.unit, locale, t),
    over: total > limit ? formatQuantity(total - limit, resource.unit, locale, t) : null,
  }
}

export function OrganizationUsagePanel({ enabled, accessPending, accessError, authorized, onRetryAccess }: { enabled: boolean; accessPending: boolean; accessError: boolean; authorized: boolean; onRetryAccess(): void }) {
  const { t, locale } = useI18n()
  const usage = useQuery<OrganizationQuotaUsage>({ queryKey: ['organization-quota-usage'], queryFn: ({ signal }) => quotasCurrentUsage({ signal }), enabled: enabled && authorized && !accessPending && !accessError, retry: false })

  return <Surface className="organization-settings-panel organization-usage-panel" hidden={!enabled}><section id="usage-settings-panel" role="tabpanel" aria-labelledby="usage-settings-tab">
    {accessPending ? <div className="usage-loading" role="status" aria-label={t('Loading…')}><Skeleton /><Skeleton /></div>
      : accessError ? <div className="usage-message"><p role="alert">{t('Settings access could not be checked.')}</p><Button onClick={onRetryAccess}>{t('Retry')}</Button></div>
        : !authorized ? <p>{t('You do not have permission to view organization usage.')}</p>
          : usage.isPending ? <div className="usage-loading" role="status" aria-label={t('Loading usage')}><Skeleton /><Skeleton /></div>
        : usage.isError ? <div className="usage-message"><p role="alert">{t('Usage could not be loaded.')}</p><Button onClick={() => void usage.refetch()}>{t('Retry')}</Button></div>
          : usage.data.resources.length === 0 ? <div className="usage-message"><h2>{t('No usage to show')}</h2><p>{t('Usage information will appear here when resources are available.')}</p></div>
            : <div className="usage-list" aria-label={t('Organization usage')}>
              {usage.data.resources.map(resource => {
                const values = usageSummary(resource, locale, t)
                const stateLabel = resource.state
                const label = t(stateLabel)
                return <article className="usage-resource" key={resource.resourceKey}>
                  <header><div><h2>{resource.name}</h2><p>{t(resource.unit === 'Bytes' ? 'Storage' : 'Counted items')}</p></div><Badge tone={stateLabel === 'Ready' ? 'success' : stateLabel === 'Unavailable' ? 'danger' : 'neutral'}>{label}</Badge></header>
                  {!resource.installed && <p className="usage-note">{t('This resource is not currently available.')}</p>}
                  {resource.state === 'Initializing' && <p className="usage-note">{t('Usage information is being prepared.')}</p>}
                  {resource.state === 'Paused' && <p className="usage-note">{t('This resource is paused.')}</p>}
                  {resource.state === 'Unavailable' && <p className="usage-note">{t('Usage information is temporarily unavailable.')}</p>}
                  {resource.mode === 'Unlimited' ? <p className="usage-unlimited">{t('Unlimited usage')}</p>
                      : resource.mode === 'Disabled' ? <p className="usage-note">{t('Usage limits are disabled.')}</p>
                      : resource.mode === 'Unconfigured' ? <p className="usage-note">{t('A usage limit has not been set.')}</p> : null}
                  <dl>
                    {resource.mode === 'Finite' && <div><dt>{t('Limit')}</dt><dd>{formatNullableQuantity(resource.limit, resource.unit, locale, t) ?? t('Not available')}</dd></div>}
                    {(resource.mode === 'Finite' || resource.mode === 'Unlimited') && <div><dt>{t('Used')}</dt><dd>{values.total ?? t('Not available')}</dd></div>}
                    {resource.mode === 'Finite' && <div><dt>{t('Remaining')}</dt><dd>{values.remaining ?? t('Not available')}</dd></div>}
                    {values.over !== null && <div className="usage-over-limit"><dt>{t('Over limit')}</dt><dd>{values.over}</dd></div>}
                    {resource.committed !== null && <div><dt>{t('Committed')}</dt><dd>{formatNullableQuantity(resource.committed, resource.unit, locale, t)}</dd></div>}
                    {resource.reserved !== null && <div><dt>{t('Reserved')}</dt><dd>{formatNullableQuantity(resource.reserved, resource.unit, locale, t)}</dd></div>}
                    {resource.reconciliationHeld !== null && <div><dt>{t('Being checked')}</dt><dd>{formatNullableQuantity(resource.reconciliationHeld, resource.unit, locale, t)}</dd></div>}
                  </dl>
                </article>
              })}
            </div>}
  </section></Surface>
}
