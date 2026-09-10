import { useState, type FormEvent } from 'react'
import { customFetch, setAntiforgeryToken } from '@trykatch/api-client'
import { Button, PasswordField } from '@trykatch/ui'
import { ProductLogo } from '../components/ProductLogo'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'
import { useI18n } from '../i18n/I18nProvider'

interface Session { hasPlatformAccess?: boolean; isPlatformAdministrator?: boolean }
interface Organization { id: string }
interface LoginPageProps { navigate?: (path: string) => void }

export function LoginPage({ navigate = path => window.location.assign(path) }: LoginPageProps = {}) {
  const { t } = useI18n()
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [mfaRequired, setMfaRequired] = useState(false)
  const [rememberMe, setRememberMe] = useState(false)
  const requestedReturnTo = new URLSearchParams(window.location.search).get('returnTo')
  const returnTo = requestedReturnTo?.startsWith('/invite/') ? requestedReturnTo : undefined

  async function prepareAntiforgery() {
    const csrf = await customFetch<{ token: string }>('/api/v1/auth/antiforgery', { method: 'GET' })
    setAntiforgeryToken(csrf.token)
  }

  async function finish(session: Session, persistent = rememberMe) {
    if (returnTo) {
      navigate(returnTo)
      return
    }
    if (session.hasPlatformAccess || session.isPlatformAdministrator) {
      navigate('/dashboard')
      return
    }

    const organizations = await customFetch<Organization[]>('/api/v1/me/organizations', { method: 'GET' })
    if (organizations[0]) {
      // Authentication rotates the cookie-bound antiforgery identity. Fetch a
      // fresh request token before the first authenticated mutation.
      await prepareAntiforgery()
      await customFetch<void>('/api/v1/workspace/select', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ organizationId: organizations[0].id, remember: persistent }),
      })
      navigate('/overview')
      return
    }

    setError(t('Your account does not have an active organization membership.'))
  }

  async function submitPassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    const form = new FormData(event.currentTarget)
    const persistent = form.get('remember') === 'on'
    setRememberMe(persistent)
    try {
      await prepareAntiforgery()
      const session = await customFetch<Session>('/api/v1/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: form.get('email'), password: form.get('password'), rememberMe: persistent }),
      })
      await finish(session, persistent)
    } catch (reason) {
      if (typeof reason === 'object' && reason !== null && 'status' in reason && reason.status === 428) {
        setMfaRequired(true)
      } else {
        setError(reason instanceof Error ? reason.message : t('Sign in failed'))
      }
    } finally {
      setBusy(false)
    }
  }

  async function submitMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    const form = new FormData(event.currentTarget)
    try {
      const session = await customFetch<Session>('/api/v1/auth/login/mfa', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          code: form.get('code'),
          isRecoveryCode: form.get('recovery') === 'on',
          rememberMe,
          rememberClient: form.get('rememberClient') === 'on',
        }),
      })
      await finish(session)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t('Multi-factor verification failed'))
    } finally {
      setBusy(false)
    }
  }

  return <main className="auth-page">
    <div className="auth-language"><LanguageSwitcher compact /></div>
    <a className="auth-brand" href="/" aria-label={`Trykatch · ${t('Home')}`}><span className="brand-mark"><ProductLogo size={17} /></span><strong>Trykatch</strong></a>
    <section className="auth-card login-card">
      <h1>{t(mfaRequired ? 'Verify your identity' : 'Sign in')}</h1>
      <p>{t(mfaRequired ? 'Enter an authenticator or recovery code.' : 'Use your verified account to continue.')}</p>
      {mfaRequired ? <form onSubmit={submitMfa}>
        <label>{t('Verification code')}<input name="code" inputMode="numeric" autoComplete="one-time-code" required autoFocus /></label>
        <label className="checkbox"><input name="recovery" type="checkbox" /> {t('This is a recovery code')}</label>
        <label className="checkbox"><input name="rememberClient" type="checkbox" /> {t('Remember this trusted browser')}</label>
        {error && <div className="form-error" role="alert">{error}</div>}
        <Button variant="primary" type="submit" disabled={busy}>{t(busy ? 'Verifying…' : 'Verify and continue')}</Button>
        <Button type="button" variant="ghost" onClick={() => setMfaRequired(false)}>{t('Use a different account')}</Button>
      </form> : <form onSubmit={submitPassword}>
        <label>{t('Email address')}<input name="email" type="email" autoComplete="email" required /></label>
        <PasswordField label={t('Password')} name="password" autoComplete="current-password" visibilityLabel={t('Password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />
        <div className="auth-form-options"><label className="checkbox"><input name="remember" type="checkbox" /> {t('Keep me signed in')}</label><a className="text-button" href="/forgot-password">{t('Forgot password?')}</a></div>
        {error && <div className="form-error" role="alert">{error}</div>}
        <Button variant="primary" type="submit" disabled={busy}>{t(busy ? 'Signing in…' : 'Sign in')}</Button>
      </form>}
    </section>
  </main>
}
