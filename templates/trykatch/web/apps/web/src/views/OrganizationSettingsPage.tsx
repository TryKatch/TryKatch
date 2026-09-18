import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch, organizationSettingsAiGet, organizationSettingsAiUpdate, organizationSettingsAiTest, type OrganizationAiConfiguration } from '@trykatch/api-client'
import { Badge, Button, FloatingInput, FloatingSelect, PageHeader, Skeleton, Surface } from '@trykatch/ui'
import { Bot, KeyRound, ShieldCheck } from 'lucide-react'
import { useState } from 'react'
import { z } from 'zod'
import { useI18n } from '../i18n/I18nProvider'

type ProviderFields = { provider: string; model: string; endpoint: string; timeoutMs: number }
const providerSchema = z.object({ provider: z.enum(['openai', 'deepseek', 'chat-completions', 'ollama']), model: z.string().trim().min(1).max(120), endpoint: z.string().max(500), timeoutMs: z.number().int().min(1000).max(60_000) })
const defaults = (configuration: OrganizationAiConfiguration): ProviderFields => configuration.usesTenantProvider
  ? { provider: configuration.provider, model: configuration.model, endpoint: configuration.endpoint, timeoutMs: Number(configuration.timeoutMs) }
  : { provider: configuration.provider || 'deepseek', model: configuration.model || '', endpoint: configuration.endpoint || (!configuration.provider || configuration.provider === 'deepseek' ? 'https://api.deepseek.com' : ''), timeoutMs: 30_000 }

export function OrganizationSettingsPage() {
  const { t } = useI18n()
  const client = useQueryClient()
  const access = useQuery({ queryKey: ['access'], queryFn: () => customFetch<{ permissions: string[] }>('/api/v1/access', { method: 'GET' }) })
  const canManage = access.data?.permissions.includes('organizations.manage') ?? false
  const canRead = canManage || (access.data?.permissions.includes('organizations.read') ?? false)
  const settings = useQuery({ queryKey: ['organization-ai-settings'], queryFn: ({ signal }) => organizationSettingsAiGet({ signal }), enabled: canRead, retry: false })
  const [draft, setDraft] = useState<boolean>()
  const [fields, setFields] = useState<ProviderFields>()
  const [apiKey, setApiKey] = useState('')
  const [removeKey, setRemoveKey] = useState(false)
  const [validation, setValidation] = useState('')
  const [saved, setSaved] = useState(false)
  const providerEdited = fields !== undefined || apiKey.length > 0 || removeKey
  const dirty = providerEdited || (draft !== undefined && draft !== settings.data?.enabled)
  const save = useMutation({ mutationFn: (current: OrganizationAiConfiguration) => organizationSettingsAiUpdate({
    enabled: draft ?? current.enabled, expectedVersion: current.version,
    ...(providerEdited ? { ...(fields ?? defaults(current)), apiKey: apiKey || undefined, removeApiKey: removeKey } : {}),
  }), onSuccess: configuration => {
    client.setQueryData(['organization-ai-settings'], configuration)
    setDraft(undefined); setFields(undefined); setRemoveKey(false); setSaved(true)
    void client.invalidateQueries({ queryKey: ['assistant-status'] })
  }, onSettled: () => setApiKey('') })
  const test = useMutation({ mutationFn: (version: string) => organizationSettingsAiTest({ expectedVersion: version }) })
  const conflict = typeof save.error === 'object' && save.error !== null && 'status' in save.error && save.error.status === 409
  const busy = save.isPending || test.isPending
  const resetFeedback = () => { setSaved(false); setValidation(''); test.reset(); if (!conflict) save.reset() }
  const reload = async () => { const result = await settings.refetch(); if (result.isSuccess) { setDraft(undefined); setFields(undefined); setApiKey(''); setRemoveKey(false); resetFeedback(); save.reset() } }
  const submit = () => {
    const current = settings.data
    if (!current || !canManage || conflict || busy || settings.isFetching || !dirty) return
    if (providerEdited) {
      const value = fields ?? defaults(current)
      const parsed = providerSchema.safeParse(value)
      const destinationChanged = !current.usesTenantProvider || value.provider !== current.provider || value.endpoint !== current.endpoint
      const keyNeeded = value.provider !== 'ollama' && (draft ?? current.enabled) && (removeKey || destinationChanged || !current.hasApiKey)
      if (!parsed.success || (value.provider !== 'openai' && !current.allowedEndpoints.some(endpoint => endpoint.replace(/\/$/, '') === value.endpoint.replace(/\/$/, '')))
        || (keyNeeded && !apiKey) || apiKey.length > 2048 || /[\r\n\u0000]/.test(apiKey)) {
        setValidation(t('Choose a provider, approved endpoint, model, valid timeout and required API key.')); return
      }
    }
    setValidation(''); test.reset(); save.mutate(current)
  }

  return <>
    <PageHeader eyebrow={t('Administration')} title={t('Settings')} description={t('Manage configuration for this organization.')} />
    <div className="user-management-tabs organization-settings-tabs" role="tablist" aria-label={t('Organization settings')}>
      <button id="ai-settings-tab" role="tab" aria-selected aria-controls="ai-settings-panel" className="active"><Bot size={16} aria-hidden="true" /> {t('AI Configuration')}</button>
    </div>
    <Surface className="organization-settings-panel"><section id="ai-settings-panel" role="tabpanel" aria-labelledby="ai-settings-tab">
      {access.isPending || (canRead && settings.isPending) ? <div role="status" aria-label={t('Loading…')}><Skeleton /><Skeleton /></div>
        : access.isError ? <><p role="alert">{t('Settings access could not be checked.')}</p><Button onClick={() => void access.refetch()}>{t('Retry')}</Button></>
        : !canRead ? <p>{t('You do not have permission to view organization settings.')}</p>
        : settings.isError ? <><p role="alert">{t('Settings could not be loaded.')}</p><Button onClick={() => void settings.refetch()}>{t('Retry')}</Button></>
        : settings.data && (() => {
          const current = settings.data
          const value = fields ?? defaults(current)
          const editable = canManage && current.canConfigureProvider && !busy
          const change = (patch: Partial<ProviderFields>) => { setFields({ ...value, ...patch }); resetFeedback() }
          return <form className="organization-ai-form" noValidate onSubmit={event => { event.preventDefault(); submit() }}>
            <div className="ai-settings-heading">
              <span className="ai-settings-icon" aria-hidden="true"><Bot size={19} /></span>
              <div><h2>{t('AI Configuration')}</h2><p>{t('Connect your organization’s AI subscription.')}</p></div>
              <Badge tone={current.enabled ? 'success' : 'neutral'}>{t(current.enabled ? 'Enabled' : 'Disabled')}</Badge>
            </div>
            {!canManage && <p className="ai-settings-notice">{t('Read-only configuration. Organization management permission is required to make changes.')}</p>}
            {!current.canConfigureProvider && <p className="ai-settings-notice" role="status">{t('The operator has disabled tenant provider configuration.')}</p>}
            <div className="ai-settings-body">
              <div className="ai-settings-fields">
                <FloatingSelect label={t('Provider')} value={value.provider} disabled={!editable} options={[
                  { value: 'deepseek', label: 'DeepSeek' },
                  { value: 'openai', label: 'OpenAI' },
                  { value: 'chat-completions', label: t('OpenAI-compatible') },
                  { value: 'ollama', label: 'Ollama' },
                ]} onValueChange={provider => {
                  setApiKey(''); setRemoveKey(false)
                  change({ provider, endpoint: provider === 'deepseek' ? 'https://api.deepseek.com' : '' })
                }} />
                <div className="ai-model-fields">
                  <FloatingInput label={t('Model')} value={value.model} maxLength={120} readOnly={!editable} onChange={event => change({ model: event.target.value })} description={t('Use the exact model ID supplied by your provider.')} />
                  <FloatingInput label={t('Timeout (milliseconds)')} type="number" value={value.timeoutMs} min={1000} max={60_000} readOnly={!editable} onChange={event => change({ timeoutMs: Number(event.target.value) })} />
                </div>
                {value.provider !== 'openai' && <FloatingInput label={t('API endpoint')} value={value.endpoint} maxLength={500} readOnly={!editable} onChange={event => change({ endpoint: event.target.value })} description={t('Only destinations approved by the platform operator are accepted.')} />}
                <section className="ai-settings-credentials" aria-labelledby="ai-credentials-heading">
                  <div className="ai-section-heading"><h3 id="ai-credentials-heading"><KeyRound size={15} aria-hidden="true" />{t('Subscription credentials')}</h3><Badge tone={current.hasApiKey ? 'success' : 'neutral'}>{t(current.hasApiKey ? 'Key saved' : 'Not configured')}</Badge></div>
                  {editable && value.provider !== 'ollama' && <FloatingInput label={t(current.hasApiKey ? 'Replacement API key' : 'API key')} type="password" value={apiKey} maxLength={2048} autoComplete="new-password" spellCheck={false} onChange={event => { setApiKey(event.target.value); setRemoveKey(false); resetFeedback() }} description={t('Write-only. Leave blank to keep the saved key. Changing provider or endpoint requires a replacement key.')} />}
                  <small>{t(current.hasApiKey ? 'API key configured. The saved key cannot be displayed.' : 'No organization API key configured.')}</small>
                  {editable && current.hasApiKey && <label className="checkbox ai-key-removal"><input type="checkbox" checked={removeKey} onChange={event => { setRemoveKey(event.target.checked); setApiKey(''); resetFeedback() }} /> {t('Remove saved API key (disable AI Help first)')}</label>}
                </section>
              </div>
              <aside className="ai-settings-guidance" aria-label={t('Activation and privacy')}>
                <section><h3><Bot size={15} aria-hidden="true" />{t('Read-only AI Help')}</h3><p>{t('Activation applies only to this organization. The assistant cannot modify your records.')}</p>
                  <label className="checkbox ai-activation-control"><input type="checkbox" checked={draft ?? current.enabled} disabled={!canManage || busy} onChange={event => { setDraft(event.target.checked); resetFeedback() }} /> {t('Enable AI Help for this organization')}</label>
                  {!current.providerAvailable && <p className="ai-settings-notice" role="status">{t('AI Help is unavailable until a valid provider configuration is saved or the platform operator enables its provider.')}</p>}
                </section>
                <section><h3><ShieldCheck size={15} aria-hidden="true" />{t('Privacy and protection')}</h3><p>{t('API keys are protected on the server, never returned by the API and cleared from this field after submission.')}</p><p>{t('Questions and authorized workspace data are sent to the configured provider. Review its privacy terms before enabling AI Help.')}</p></section>
                {!current.usesTenantProvider && <p className="ai-platform-hint">{t('Save your own provider below to use this organization’s subscription. Until then, activation uses the platform configuration if available.')}</p>}
              </aside>
            </div>
            {(validation || save.error || saved || test.isSuccess || test.error) && <div className="ai-settings-feedback">
              {validation && <p className="form-error" role="alert">{validation}</p>}
              {save.error && <p className="form-error" role="alert">{t(conflict ? 'Settings changed. Reload the current settings before saving.' : 'Settings could not be saved. Your selection is preserved.')} {t('Re-enter a new API key to retry if you supplied one.')}</p>}
              {saved && <p role="status">{t('Settings saved.')}</p>}
              {test.isSuccess && <p role="status">{t('Connection successful.')}</p>}
              {test.error && <p className="form-error" role="alert">{t('Connection failed. Check the saved endpoint, model and API key.')}</p>}
            </div>}
            {canManage && <div className="ai-settings-footer">
              <small>{t('Test connection sends a small billable request to the saved provider, without workspace data. Save changes first.')}</small>
              <div className="ai-settings-actions">
                {conflict && <Button type="button" variant="secondary" disabled={settings.isFetching || busy} onClick={() => void reload()}>{t('Reload settings')}</Button>}
                <Button type="button" variant="secondary" disabled={busy || settings.isFetching || dirty || conflict || !current.usesTenantProvider || !current.providerAvailable} onClick={() => test.mutate(current.version)}>{t(test.isPending ? 'Testing…' : 'Test connection')}</Button>
                <Button type="submit" variant="primary" disabled={busy || settings.isFetching || conflict || !dirty}>{t(save.isPending ? 'Saving…' : 'Save changes')}</Button>
              </div>
            </div>}
          </form>
        })()}
    </section></Surface>
  </>
}
