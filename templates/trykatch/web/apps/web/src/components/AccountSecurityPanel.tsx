import { customFetch, type MfaRecoveryCodes, type PendingMfaSetup, type RecentAssuranceGrant, type ReauthenticationRequest } from '@trykatch/api-client'
import { Badge, Button, PasswordField, Surface } from '@trykatch/ui'
import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { useI18n } from '../i18n/I18nProvider'
import { useAccountSecurityCompletion } from './AccountSecurityCompletion'

type Purpose = 'mfa.enroll' | 'mfa.replace' | 'mfa.disable' | 'mfa.recovery-codes'
type Step = { kind: 'idle' } | { kind: 'verify'; purpose: Purpose } | { kind: 'enrollment'; setup: PendingMfaSetup }

function failureMessage(error: unknown): string {
  const problem = error && typeof error === 'object' && 'problem' in error ? error.problem : undefined
  const code = problem && typeof problem === 'object' && 'title' in problem ? problem.title : undefined
  switch (code) {
    case 'reauthentication_unsupported': return 'This account has no supported local verification method. Contact your administrator for identity-provider recovery.'
    case 'grant_expired': return 'Verification expired. Start again and verify your identity.'
    case 'enrollment_conflict': return 'Enrollment has changed or another setup is pending. Start again, or wait ten minutes for the pending setup to expire.'
    default: return 'Verification failed. Check your credentials and try again, or sign in again if your session has changed.'
  }
}

// Proofs/grants stay in this component or an in-flight request. Recovery codes
// move only to the root memory-only completion boundary, outside auth redirects.
export function AccountSecurityPanel({ twoFactorEnabled, disabled = false }: { twoFactorEnabled: boolean; disabled?: boolean }) {
  const { t } = useI18n()
  const complete = useAccountSecurityCompletion()
  const heading = useId()
  const [step, setStep] = useState<Step>({ kind: 'idle' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [recoveryFactor, setRecoveryFactor] = useState(false)
  const request = useRef<AbortController | null>(null)
  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => {
    if (step.kind !== 'enrollment') return
    const timer = window.setTimeout(() => {
      setStep({ kind: 'idle' })
      setError('Verification expired. Start again and verify your identity.')
    }, Math.max(0, Date.parse(step.setup.expiresAt) - Date.now()))
    return () => window.clearTimeout(timer)
  }, [step])

  async function run(operation: (signal: AbortSignal) => Promise<void>) {
    const controller = new AbortController()
    request.current = controller
    setBusy(true)
    setError(undefined)
    try { await operation(controller.signal) }
    catch (failure) { if (!controller.signal.aborted) setError(failureMessage(failure)) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }

  function post<T>(path: string, body: unknown, signal: AbortSignal) {
    return customFetch<T>(`/api/v1/account/security${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal })
  }

  function choose(purpose: Purpose) {
    setError(undefined)
    setRecoveryFactor(false)
    setStep({ kind: 'verify', purpose })
  }

  function verify(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (step.kind !== 'verify' || busy) return
    const purpose = step.purpose
    const form = new FormData(event.currentTarget)
    const proof: ReauthenticationRequest = { purpose, password: String(form.get('password')), code: twoFactorEnabled ? String(form.get('factor')) : null, isRecoveryCode: twoFactorEnabled && recoveryFactor }
    event.currentTarget.reset()
    void run(async (signal) => {
      const assurance = await post<RecentAssuranceGrant>('/reauthenticate', proof, signal)
      if (purpose === 'mfa.enroll' || purpose === 'mfa.replace') {
        const setup = await post<PendingMfaSetup>('/mfa/setup', { grant: assurance.grant }, signal)
        setStep({ kind: 'enrollment', setup })
      } else if (purpose === 'mfa.recovery-codes') {
        const result = await post<MfaRecoveryCodes>('/mfa/recovery-codes', { grant: assurance.grant }, signal)
        complete(result.codes)
      } else {
        await post<void>('/mfa/disable', { grant: assurance.grant }, signal)
        complete([])
      }
    })
  }

  function confirm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (step.kind !== 'enrollment' || busy) return
    const enrollmentId = step.setup.enrollmentId
    const code = String(new FormData(event.currentTarget).get('code'))
    event.currentTarget.reset()
    void run(async (signal) => {
      const result = await post<MfaRecoveryCodes>('/mfa/enable', { enrollmentId, code }, signal)
      complete(result.codes)
    })
  }

  function cancel() {
    if (busy) return
    const enrollmentId = step.kind === 'enrollment' ? step.setup.enrollmentId : undefined
    setStep({ kind: 'idle' })
    setError(undefined)
    if (enrollmentId) void run(async (signal) => { await post<void>('/mfa/cancel', { enrollmentId }, signal) })
  }

  return <Surface className="profile-card account-security-card" aria-busy={busy || disabled}>
    <div className="profile-card-heading"><div><h2 id={heading}>{t('Two-factor authentication')}</h2><p>{t('Add a time-based one-time password to protect your account.')}</p></div><Badge tone={twoFactorEnabled ? 'success' : 'warning'}>{t(twoFactorEnabled ? 'Enabled' : 'Not enabled')}</Badge></div>
    <section className="security-panel" aria-labelledby={heading}>
      {error && <div className="form-error" role="alert">{t(error)}</div>}
      {step.kind === 'idle' && <div className="security-status">
        <p>{t(twoFactorEnabled ? 'Your authenticator is active. Sensitive changes require your password and a current authenticator or recovery code.' : 'Verify your password before setting up an authenticator.')}</p>
        <div className="form-actions">
          <Button variant="primary" disabled={disabled || busy} onClick={() => choose(twoFactorEnabled ? 'mfa.replace' : 'mfa.enroll')}>{t(twoFactorEnabled ? 'Replace authenticator' : 'Set up MFA')}</Button>
          {twoFactorEnabled && <><Button disabled={disabled || busy} onClick={() => choose('mfa.recovery-codes')}>{t('Regenerate recovery codes')}</Button><Button variant="danger" disabled={disabled || busy} onClick={() => choose('mfa.disable')}>{t('Disable MFA')}</Button></>}
        </div>
      </div>}
      {step.kind === 'verify' && <form className="dialog-form" onSubmit={verify} aria-label={t('Verify your identity')}>
        <h3>{t('Verify your identity')}</h3>
        <p>{t('Verification is valid for one action. Completing an MFA change signs you out.')}</p>
        {step.purpose === 'mfa.disable' && <p>{t('Disabling MFA removes an important layer of account protection.')}</p>}
        <PasswordField label={t('Current password')} name="password" autoComplete="current-password" maxLength={1024} visibilityLabel={t('Current password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required autoFocus disabled={busy} />
        {twoFactorEnabled && <>
          <label><input type="checkbox" checked={recoveryFactor} disabled={busy} onChange={(event) => setRecoveryFactor(event.target.checked)} /> {t('Use a recovery code')}</label>
          <label>{t(recoveryFactor ? 'Recovery code' : 'Authenticator code')}<input key={String(recoveryFactor)} name="factor" autoComplete="one-time-code" inputMode={recoveryFactor ? 'text' : 'numeric'} maxLength={128} required disabled={busy} /></label>
        </>}
        <div className="form-actions"><Button variant="ghost" type="button" disabled={busy} onClick={cancel}>{t('Cancel')}</Button><Button variant="primary" type="submit" disabled={busy}>{t(busy ? 'Verifying…' : 'Verify and continue')}</Button></div>
      </form>}
      {step.kind === 'enrollment' && <form className="dialog-form" onSubmit={confirm} aria-label={t('Confirm authenticator')}>
        <p>{t('Enter this new key in your authenticator. It is shown only now and expires in ten minutes.')}</p><code>{step.setup.sharedKey}</code>
        {twoFactorEnabled && <p>{t('Your existing authenticator remains active until you confirm the new one.')}</p>}
        <label>{t('Six-digit code')}<input name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,8}" maxLength={8} required autoFocus disabled={busy} /></label>
        <div className="form-actions"><Button variant="ghost" type="button" disabled={busy} onClick={cancel}>{t('Cancel')}</Button><Button variant="primary" type="submit" disabled={busy}>{t(busy ? 'Verifying…' : 'Confirm authenticator')}</Button></div>
      </form>}
    </section>
  </Surface>
}
