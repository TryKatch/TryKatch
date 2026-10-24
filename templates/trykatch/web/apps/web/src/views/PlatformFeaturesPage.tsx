import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, organizationsList, platformFeaturesHistory, platformFeaturesList, platformFeaturesSaveEnvironment, platformFeaturesSaveOrganization, type FeatureAdministrationItem, type FeatureAdministrationSnapshot, type FeatureControlHistoryEntry } from '@trykatch/api-client'
import { Badge, Button, DataTable, Dialog, EmptyState, FloatingSelect, PageHeader, Skeleton, Surface, type DataTableColumn } from '@trykatch/ui'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useI18n } from '../i18n/I18nProvider'
import { useDataTableLabels } from '../i18n/useDataTableLabels'

interface PlatformSession { platformPermissions?: string[] }
interface Draft { environment?: boolean | null; organization?: boolean | null; forceDisabled?: boolean }
interface DraftVersions { environment?: string; organization?: string }
const PAGE_SIZE = 25
const DIRECTORY_PAGE_SIZE = 25
const EMPTY_VERSION = '00000000-0000-0000-0000-000000000000'
const INHERIT = '__inherit__'
const versionOrEmpty = (version?: string | null) => version?.trim() ? version : EMPTY_VERSION
const optionValue = (value: boolean | null) => value === null ? INHERIT : value ? 'enabled' : 'disabled'
const parseOption = (value: string): boolean | null => value === INHERIT ? null : value === 'enabled'

function isConflict(error: unknown) { return typeof error === 'object' && error !== null && 'status' in error && error.status === 409 }
function isBusy(error: unknown) {
  if (typeof error !== 'object' || error === null) return false
  const candidate = error as { code?: string; title?: string; detail?: string; message?: string; problem?: { code?: string; title?: string; detail?: string } }
  return [candidate.code, candidate.title, candidate.detail, candidate.message, candidate.problem?.code, candidate.problem?.title, candidate.problem?.detail].some(value => value?.includes('feature_control_busy'))
}
function effectiveValue(item: FeatureAdministrationItem) {
  if (item.deploymentDisabled || item.environmentControl.forceDisabled) return 'forced'
  const value = item.organizationControl?.enabled ?? item.environmentControl.enabled ?? item.configuredDefault ?? item.declaration.defaultEnabled
  return value ? 'enabled' : 'disabled'
}
function historyControlValue(enabled: boolean | null, forceDisabled: boolean | null, t: (message: string) => string) {
  const setting = enabled === null ? t('Inherited') : enabled ? t('Enabled') : t('Disabled')
  return forceDisabled === null ? setting : `${setting} · ${t(forceDisabled ? 'Forced off' : 'Not forced off')}`
}

export function PlatformFeaturesPage({ organizationId: initialOrganizationId }: { organizationId?: string }) {
  const { t, formatDate } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const client = useQueryClient()
  const [organizationId, setOrganizationId] = useState(initialOrganizationId ?? '')
  const [directoryPage, setDirectoryPage] = useState(1)
  const [displayed, setDisplayed] = useState<FeatureAdministrationSnapshot>()
  const [drafts, setDrafts] = useState<Record<string, Draft>>({})
  const [draftVersions, setDraftVersions] = useState<Record<string, DraftVersions>>({})
  const [blocked, setBlocked] = useState<Record<string, 'conflict' | 'busy'>>({})
  const [success, setSuccess] = useState<Record<string, boolean>>({})
  const [historyTarget, setHistoryTarget] = useState<FeatureAdministrationItem>()
  const [historyPage, setHistoryPage] = useState(1)

  const changeOrganization = useCallback((nextOrganizationId: string) => {
    setOrganizationId(nextOrganizationId)
    setDisplayed(undefined)
    setDrafts({})
    setDraftVersions({})
    setBlocked({})
    setSuccess({})
    setHistoryTarget(undefined)
    setHistoryPage(1)
  }, [])
  useEffect(() => { changeOrganization(initialOrganizationId ?? '') }, [initialOrganizationId, changeOrganization])
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<PlatformSession>('/api/v1/auth/session', { method: 'GET' }), retry: false })
  const canRead = session.isSuccess && session.data.platformPermissions?.includes('platform.features.read') === true
  const canManage = session.isSuccess && session.data.platformPermissions?.includes('platform.features.manage') === true
  const canReadTenants = session.isSuccess && session.data.platformPermissions?.includes('platform.tenants.read') === true
  const features = useQuery({
    queryKey: ['platform', 'features', organizationId || null],
    enabled: canRead,
    retry: false,
    queryFn: ({ signal }) => platformFeaturesList(organizationId ? { organizationId } : undefined, { signal }),
  })
  const tenants = useQuery({
    queryKey: ['platform', 'feature-target-organizations', directoryPage],
    enabled: canRead && canReadTenants,
    retry: false,
    queryFn: ({ signal }) => organizationsList({ page: directoryPage, pageSize: DIRECTORY_PAGE_SIZE }, { signal }),
  })

  useEffect(() => {
    if (!blocked || Object.keys(blocked).length === 0) setDisplayed(features.data)
  }, [features.data, blocked])
  const targetSnapshot = displayed?.organizationId === (organizationId || null) ? displayed : undefined

  const history = useQuery({
    queryKey: ['platform', 'feature-history', historyTarget?.declaration.key, organizationId || null, historyPage],
    enabled: Boolean(canRead && historyTarget),
    retry: false,
    queryFn: ({ signal }) => platformFeaturesHistory(historyTarget!.declaration.key, { page: historyPage, pageSize: PAGE_SIZE, ...(organizationId ? { organizationId } : {}) }, { signal }),
  })
  const saveEnvironment = useMutation({
    mutationFn: ({ item, value, forceDisabled, expectedVersion }: { item: FeatureAdministrationItem; value: boolean | null; forceDisabled: boolean; expectedVersion: string }) => platformFeaturesSaveEnvironment(item.declaration.key, {
      defaultEnabled: value,
      forceDisabled,
      expectedVersion,
    }),
    onSuccess: async (_result, { item }) => {
      setSuccess(value => ({ ...value, [item.declaration.key]: true }))
      setDrafts(value => { const next = { ...value }; const draft = { ...next[item.declaration.key] }; delete draft.environment; delete draft.forceDisabled; if (Object.keys(draft).length) next[item.declaration.key] = draft; else delete next[item.declaration.key]; return next })
      setDraftVersions(value => { const next = { ...value }; const versions = { ...next[item.declaration.key] }; delete versions.environment; if (!versions.organization) delete next[item.declaration.key]; else next[item.declaration.key] = versions; return next })
      setBlocked(value => { const next = { ...value }; delete next[item.declaration.key]; return next })
      await client.invalidateQueries({ queryKey: ['platform', 'features'] })
    },
    onError: (error, { item }) => {
      if (isConflict(error)) setBlocked(value => ({ ...value, [item.declaration.key]: isBusy(error) ? 'busy' : 'conflict' }))
    },
  })
  const saveOrganization = useMutation({
    mutationFn: ({ item, value, expectedVersion, targetOrganizationId }: { item: FeatureAdministrationItem; value: boolean | null; expectedVersion: string; targetOrganizationId: string }) => platformFeaturesSaveOrganization(item.declaration.key, targetOrganizationId, {
      enabled: value,
      expectedVersion,
    }),
    onSuccess: async (_result, { item }) => {
      setSuccess(value => ({ ...value, [item.declaration.key]: true }))
      setDrafts(value => { const next = { ...value }; const draft = { ...next[item.declaration.key] }; delete draft.organization; if (Object.keys(draft).length) next[item.declaration.key] = draft; else delete next[item.declaration.key]; return next })
      setDraftVersions(value => { const next = { ...value }; const versions = { ...next[item.declaration.key] }; delete versions.organization; if (!versions.environment) delete next[item.declaration.key]; else next[item.declaration.key] = versions; return next })
      setBlocked(value => { const next = { ...value }; delete next[item.declaration.key]; return next })
      await client.invalidateQueries({ queryKey: ['platform', 'features'] })
    },
    onError: (error, { item }) => {
      if (isConflict(error)) setBlocked(value => ({ ...value, [item.declaration.key]: isBusy(error) ? 'busy' : 'conflict' }))
    },
  })
  const busy = saveEnvironment.isPending || saveOrganization.isPending
  const reloadControls = async () => {
    const result = await features.refetch()
    if (result.isSuccess) {
      setDisplayed(result.data)
      setDraftVersions(current => {
        const refreshed = { ...current }
        for (const [key, draft] of Object.entries(drafts)) {
          const item = result.data.items.find(candidate => candidate.declaration.key === key)
          if (!item) continue
          refreshed[key] = {
            ...(draft.environment !== undefined || draft.forceDisabled !== undefined ? { environment: versionOrEmpty(item.environmentControl.version) } : {}),
            ...(draft.organization !== undefined ? { organization: versionOrEmpty(item.organizationControl?.version) } : {}),
          }
        }
        return refreshed
      })
      setBlocked({})
    }
  }
  const captureDraftVersion = useCallback((key: string, scope: keyof DraftVersions, version: string | undefined) => {
    setDraftVersions(current => ({ ...current, [key]: { ...current[key], [scope]: current[key]?.[scope] ?? versionOrEmpty(version) } }))
  }, [])

  const columns = useMemo<DataTableColumn<FeatureAdministrationItem>[]>(() => [
    { id: 'feature', header: t('Feature'), hideable: false, cell: item => <div><strong>{t(item.declaration.name)}</strong><small>{t(item.declaration.description)}</small></div>, searchValue: item => `${item.moduleId} ${item.declaration.key} ${item.declaration.name}` },
    { id: 'effective', header: t('Availability'), cell: item => <Badge tone={effectiveValue(item) === 'enabled' ? 'success' : effectiveValue(item) === 'forced' ? 'danger' : 'neutral'}>{t(effectiveValue(item) === 'forced' ? 'Forced off' : effectiveValue(item) === 'enabled' ? 'Enabled' : 'Disabled')}</Badge> },
    { id: 'source', header: t('Control source'), cell: item => t(item.deploymentDisabled ? 'Deployment disabled' : item.environmentControl.forceDisabled ? 'Force disabled by operator' : item.organizationControl?.enabled !== null && item.organizationControl?.enabled !== undefined ? 'Organization override' : item.environmentControl.enabled !== null ? 'Environment control' : item.configuredDefault !== null ? 'Configured default' : 'Declaration default') },
    ...(canManage ? [{ id: 'controls', header: t('Controls'), hideable: false as const, cell: (item: FeatureAdministrationItem) => {
      const current = targetSnapshot?.items.find(candidate => candidate.declaration.key === item.declaration.key)
      const draft = drafts[item.declaration.key] ?? {}
      const environmentValue = draft.environment === undefined ? current?.environmentControl.enabled ?? null : draft.environment
      const organizationValue = draft.organization === undefined ? current?.organizationControl?.enabled ?? null : draft.organization
      const forceDisabledValue = draft.forceDisabled ?? current?.environmentControl.forceDisabled ?? false
      const featureBlocked = blocked[item.declaration.key]
      const pending = (saveEnvironment.isPending && saveEnvironment.variables?.item.declaration.key === item.declaration.key) || (saveOrganization.isPending && saveOrganization.variables?.item.declaration.key === item.declaration.key)
      return <div className="platform-feature-controls">
        <FloatingSelect label={t('Environment control for {name}', { name: t(item.declaration.name) })} value={optionValue(environmentValue)} disabled={pending || Boolean(featureBlocked) || !current} options={[{ value: INHERIT, label: t('Inherit configured default') }, { value: 'enabled', label: t('Enabled') }, { value: 'disabled', label: t('Disabled') }]} onValueChange={value => { captureDraftVersion(item.declaration.key, 'environment', current?.environmentControl.version); saveEnvironment.reset(); setDrafts(state => ({ ...state, [item.declaration.key]: { ...state[item.declaration.key], environment: parseOption(value) } })); setSuccess(state => ({ ...state, [item.declaration.key]: false })) }} />
        <label className="checkbox"><input type="checkbox" checked={forceDisabledValue} disabled={pending || Boolean(featureBlocked) || !current} onChange={event => { captureDraftVersion(item.declaration.key, 'environment', current?.environmentControl.version); saveEnvironment.reset(); setDrafts(state => ({ ...state, [item.declaration.key]: { ...state[item.declaration.key], forceDisabled: event.target.checked } })); setSuccess(state => ({ ...state, [item.declaration.key]: false })) }} /> {t('Force feature off')}</label>
        {organizationId && <FloatingSelect label={t('Organization override for {name}', { name: t(item.declaration.name) })} value={optionValue(organizationValue)} disabled={pending || Boolean(featureBlocked) || !current?.organizationControl} options={[{ value: INHERIT, label: t('Inherit environment') }, { value: 'enabled', label: t('Enabled') }, { value: 'disabled', label: t('Disabled') }]} onValueChange={value => { captureDraftVersion(item.declaration.key, 'organization', current?.organizationControl?.version); saveOrganization.reset(); setDrafts(state => ({ ...state, [item.declaration.key]: { ...state[item.declaration.key], organization: parseOption(value) } })); setSuccess(state => ({ ...state, [item.declaration.key]: false })) }} />}
        <div className="platform-feature-actions">
          <Button variant="secondary" disabled={pending || Boolean(featureBlocked) || !current || (environmentValue === current.environmentControl.enabled && forceDisabledValue === current.environmentControl.forceDisabled) || !targetSnapshot} onClick={() => { if (!current) return; setSuccess(state => ({ ...state, [item.declaration.key]: false })); saveEnvironment.mutate({ item: current, value: environmentValue ?? null, forceDisabled: forceDisabledValue, expectedVersion: draftVersions[item.declaration.key]?.environment ?? versionOrEmpty(current.environmentControl.version) }) }}>{t(pending ? 'Saving…' : 'Save environment control for {name}', { name: t(item.declaration.name) })}</Button>
          {organizationId && <Button variant="secondary" disabled={pending || Boolean(featureBlocked) || !current?.organizationControl || organizationValue === current.organizationControl.enabled || !targetSnapshot} onClick={() => { if (!current) return; setSuccess(state => ({ ...state, [item.declaration.key]: false })); saveOrganization.mutate({ item: current, targetOrganizationId: organizationId, value: organizationValue ?? null, expectedVersion: draftVersions[item.declaration.key]?.organization ?? versionOrEmpty(current.organizationControl?.version) }) }}>{t('Save organization override for {name}', { name: t(item.declaration.name) })}</Button>}
        </div>
        {featureBlocked && <p role="alert" className="form-error">{t(featureBlocked === 'busy' ? 'Feature controls are busy. Reload controls before saving again.' : 'Feature settings changed. Reload controls before saving again.')}</p>}
        {(saveEnvironment.isError && saveEnvironment.variables?.item.declaration.key === item.declaration.key && !isConflict(saveEnvironment.error)) || (saveOrganization.isError && saveOrganization.variables?.item.declaration.key === item.declaration.key && !isConflict(saveOrganization.error)) ? <p role="alert" className="form-error">{t('Feature control could not be saved. Your selection is preserved.')}</p> : null}
        {success[item.declaration.key] && <p role="status" className="form-success">{t('Settings saved.')}</p>}
      </div>
    } }] : []),
    { id: 'history', header: t('History'), hideable: false, cell: item => <Button variant="ghost" disabled={!canRead} onClick={() => { setHistoryPage(1); setHistoryTarget(item) }}>{t('View history for {name}', { name: t(item.declaration.name) })}</Button> },
  ], [blocked, canManage, canRead, captureDraftVersion, draftVersions, drafts, organizationId, saveEnvironment, saveOrganization, success, t, targetSnapshot])

  const totalCount = Number(history.data?.totalCount ?? 0)
  const historyPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE))
  const directoryCount = Number(tenants.data?.totalCount ?? 0)
  const directoryPages = Math.max(1, Math.ceil(directoryCount / DIRECTORY_PAGE_SIZE))
  const historyColumns = useMemo<DataTableColumn<FeatureControlHistoryEntry>[]>(() => [
    { id: 'previous', header: t('Previous setting'), cell: item => historyControlValue(item.previousEnabled, item.previousForceDisabled, t) },
    { id: 'changed', header: t('Current setting'), cell: item => historyControlValue(item.enabled, item.forceDisabled, t) },
    { id: 'actor', header: t('Changed by'), cell: item => item.actorId },
    { id: 'date', header: t('Date'), cell: item => formatDate(item.changedAtUtc, { dateStyle: 'medium', timeStyle: 'short' }) },
  ], [formatDate, t])

  return <>
    <PageHeader eyebrow={t('Platform administration')} title={t('Feature controls')} description={t('Review feature availability by environment and organization.')} />
    {session.isError && <p role="alert" className="form-error">{t('Feature access could not be checked.')}</p>}
    {!session.isError && !session.isLoading && !canRead && <p role="alert" className="form-error">{t('You do not have permission to view feature controls.')}</p>}
    {canRead && <Surface className="platform-features-panel">
      <section aria-label={t('Feature controls')}>
        {targetSnapshot && <p role="status">{t('Current environment: {environment}', { environment: targetSnapshot.environment })}</p>}
        {!canManage && <p role="status">{t('Read-only access')}</p>}
        {canReadTenants && <div className="filter-bar">
          {tenants.isError ? <p role="alert" className="form-error">{t('Organizations could not be loaded.')}</p> : tenants.data && <>
            <FloatingSelect label={t('Organization override target')} disabled={busy} value={organizationId || '__environment__'} onValueChange={value => changeOrganization(value === '__environment__' ? '' : value)} options={[{ value: '__environment__', label: t('Environment only') }, ...(organizationId && !tenants.data.items.some(tenant => tenant.id === organizationId) ? [{ value: organizationId, label: t('Linked organization ({id})', { id: organizationId }) }] : []), ...tenants.data.items.map(tenant => ({ value: tenant.id, label: tenant.name }))]} />
            <div className="table-pagination"><Button variant="secondary" disabled={busy || directoryPage <= 1} onClick={() => setDirectoryPage(value => value - 1)}>{t('Previous')}</Button><span>{t('Page {page} of {count}', { page: directoryPage, count: directoryPages })}</span><Button variant="secondary" disabled={busy || directoryPage >= directoryPages} onClick={() => setDirectoryPage(value => value + 1)}>{t('Next')}</Button></div>
          </>}
        </div>}
        {features.isError && <p role="alert" className="form-error">{t('Feature controls could not be loaded.')} <Button variant="ghost" onClick={() => void features.refetch()}>{t('Retry loading')}</Button></p>}
        {features.isPending || !targetSnapshot ? <div role="status" aria-label={t('Loading feature controls')}><Skeleton /><Skeleton /></div> : targetSnapshot && <>
          {targetSnapshot.items.length === 0 ? <EmptyState title={t('No feature controls')} description={t('No declared platform features are available.')} /> : <DataTable ariaLabel={t('Feature controls')} data={targetSnapshot.items} columns={columns} getRowId={item => item.declaration.key} labels={dataTableLabels} empty={<EmptyState title={t('No matching features')} description={t('Change the search to find a feature.')} />} />}
          {blocked && Object.keys(blocked).length > 0 && <Button variant="secondary" disabled={features.isFetching || busy} onClick={() => void reloadControls()}>{t('Reload controls')}</Button>}
        </>}
      </section>
    </Surface>}
    <Dialog open={Boolean(canRead && historyTarget)} onOpenChange={open => { if (!open) setHistoryTarget(undefined) }} title={historyTarget ? t('Feature history: {name}', { name: t(historyTarget.declaration.name) }) : ''} description={historyTarget ? t('Immutable record of feature-control changes.') : undefined}>
      {history.isError && <p role="alert" className="form-error">{t('Feature history could not be loaded.')} <Button variant="ghost" onClick={() => void history.refetch()}>{t('Retry loading')}</Button></p>}
      {history.isPending ? <div role="status" aria-label={t('Loading feature history')}><Skeleton /></div> : history.data && <>
        {history.data.items.length === 0 ? <EmptyState title={t('No changes recorded')} description={t('Feature-control changes will appear here.')} /> : <DataTable ariaLabel={t('Feature history')} data={history.data.items} columns={historyColumns} getRowId={item => item.id} labels={dataTableLabels} searchable={false} />}
        <div className="table-pagination"><span>{t('Showing {start}–{end} of {total} changes', { start: totalCount ? (historyPage - 1) * PAGE_SIZE + 1 : 0, end: Math.min(historyPage * PAGE_SIZE, totalCount), total: totalCount })}</span><div><Button variant="secondary" disabled={historyPage <= 1} onClick={() => setHistoryPage(value => value - 1)}>{t('Previous')}</Button><span>{t('Page {page} of {count}', { page: historyPage, count: historyPages })}</span><Button variant="secondary" disabled={historyPage >= historyPages} onClick={() => setHistoryPage(value => value + 1)}>{t('Next')}</Button></div></div>
      </>}
    </Dialog>
  </>
}
