import { useState, type FormEvent } from 'react'
import { customFetch, setAntiforgeryToken } from '@flatpackapp/api-client'
import { Button } from '@flatpackapp/ui'
import { FlatpackLogo } from '../components/FlatpackLogo'

interface Session { hasPlatformAccess?: boolean; isPlatformAdministrator?: boolean }
interface Organization { id: string }

export function LoginPage() {
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
      window.location.assign(returnTo)
      return
    }
    if (session.hasPlatformAccess || session.isPlatformAdministrator) {
      window.location.assign('/dashboard')
      return
    }

    const organizations = await customFetch<Organization[]>('/api/v1/me/organizations', { method: 'GET' })
    if (organizations[0]) {
      await customFetch<void>('/api/v1/workspace/select', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ organizationId: organizations[0].id, remember: persistent }),
      })
      window.location.assign('/overview')
      return
    }

    setError('Your account does not have an active organization membership.')
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
        setError(reason instanceof Error ? reason.message : 'Sign in failed')
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
      setError(reason instanceof Error ? reason.message : 'Multi-factor verification failed')
    } finally {
      setBusy(false)
    }
  }

  return <main className="auth-page">
    <a className="auth-brand" href="/login" aria-label="Flatpack sign in"><span className="brand-mark"><FlatpackLogo size={17} /></span><strong>Flatpack</strong></a>
    <section className="auth-card">
      <h1>{mfaRequired ? 'Verify your identity' : 'Sign in'}</h1>
      <p>{mfaRequired ? 'Enter an authenticator or recovery code.' : 'Use your verified account to continue.'}</p>
      {mfaRequired ? <form onSubmit={submitMfa}>
        <label>Verification code<input name="code" inputMode="numeric" autoComplete="one-time-code" required autoFocus /></label>
        <label className="checkbox"><input name="recovery" type="checkbox" /> This is a recovery code</label>
        <label className="checkbox"><input name="rememberClient" type="checkbox" /> Remember this trusted browser</label>
        {error && <div className="form-error" role="alert">{error}</div>}
        <Button variant="primary" type="submit" disabled={busy}>{busy ? 'Verifying…' : 'Verify and continue'}</Button>
        <Button type="button" variant="ghost" onClick={() => setMfaRequired(false)}>Use a different account</Button>
      </form> : <form onSubmit={submitPassword}>
        <label>Email address<input name="email" type="email" autoComplete="email" required /></label>
        <label>Password<input name="password" type="password" autoComplete="current-password" required /></label>
        <div className="auth-form-options"><label className="checkbox"><input name="remember" type="checkbox" /> Keep me signed in</label><a className="text-button" href="/forgot-password">Forgot password?</a></div>
        {error && <div className="form-error" role="alert">{error}</div>}
        <Button variant="primary" type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</Button>
      </form>}
    </section>
  </main>
}
