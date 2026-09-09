import { customFetch, type ForgotPasswordResponse } from '@trykatchapp/api-client'
import { Button } from '@trykatchapp/ui'
import { useState, type FormEvent } from 'react'
import { TrykatchLogo } from '../components/TrykatchLogo'

export function ForgotPasswordPage() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [result, setResult] = useState<ForgotPasswordResponse>()

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    const form = new FormData(event.currentTarget)
    try {
      setResult(await customFetch<ForgotPasswordResponse>('/api/v1/auth/password/forgot', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: form.get('email') }),
      }))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Password reset could not be requested.')
    } finally {
      setBusy(false)
    }
  }

  return <main className="auth-page">
    <a className="auth-brand" href="/login" aria-label="Trykatch sign in"><span className="brand-mark"><TrykatchLogo size={17} /></span><strong>Trykatch</strong></a>
    <section className="auth-card" aria-labelledby="forgot-password-title">
      {result ? <div className="recovery-result" role="status">
        <h1 id="forgot-password-title">Check your email</h1>
        <p>{result.message}</p>
        {result.developmentResetUrl && <Button asChild variant="primary"><a href={result.developmentResetUrl}>Continue in local development</a></Button>}
        {!result.deliveryConfigured && !result.developmentResetUrl && <small>Email delivery is not configured. Contact your administrator.</small>}
        <a className="auth-link" href="/login">Back to sign in</a>
      </div> : <>
        <h1 id="forgot-password-title">Forgot your password?</h1>
        <p>Enter your email address and we’ll send you a secure reset link.</p>
        <form onSubmit={submit}>
          <label>Email address<input name="email" type="email" autoComplete="email" required autoFocus /></label>
          {error && <div className="form-error" role="alert">{error}</div>}
          <Button type="submit" variant="primary" disabled={busy}>{busy ? 'Sending…' : 'Send reset link'}</Button>
        </form>
        <a className="auth-link" href="/login">Back to sign in</a>
      </>}
    </section>
  </main>
}
