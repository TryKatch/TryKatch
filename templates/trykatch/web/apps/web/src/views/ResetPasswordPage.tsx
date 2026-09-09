import { customFetch } from '@trykatchapp/api-client'
import { Button, PasswordField } from '@trykatchapp/ui'
import { useState, type FormEvent } from 'react'
import { TrykatchLogo } from '../components/TrykatchLogo'

export function ResetPasswordPage() {
  const parameters = new URLSearchParams(window.location.search)
  const email = parameters.get('email') ?? ''
  const token = parameters.get('token') ?? ''
  const [busy, setBusy] = useState(false)
  const [complete, setComplete] = useState(false)
  const [error, setError] = useState<string>()

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    const form = new FormData(event.currentTarget)
    try {
      await customFetch<void>('/api/v1/auth/password/reset', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          email,
          token,
          newPassword: form.get('password'),
          confirmPassword: form.get('confirmPassword'),
        }),
      })
      setComplete(true)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Your password could not be reset.')
    } finally {
      setBusy(false)
    }
  }

  return <main className="auth-page">
    <a className="auth-brand" href="/login" aria-label="Trykatch sign in"><span className="brand-mark"><TrykatchLogo size={17} /></span><strong>Trykatch</strong></a>
    <section className="auth-card">
      {complete ? <div className="recovery-complete" role="status">
        <h1>Password updated</h1>
        <p>Your previous password and active sessions are no longer valid.</p>
        <Button type="button" variant="primary" onClick={() => window.location.assign('/login')}>Return to sign in</Button>
      </div> : <>
        <h1>Choose a new password</h1>
        <p>Use at least 12 characters and avoid a password used elsewhere.</p>
        {!email || !token ? <div className="form-error" role="alert">This password reset link is incomplete. Request a new link from the sign-in page.</div> : <form onSubmit={submit}>
          <label className="sr-only">Email address<input name="email" type="email" autoComplete="username" value={email} readOnly /></label>
          <PasswordField label="New password" name="password" autoComplete="new-password" minLength={12} visibilityLabel="new password" required autoFocus />
          <PasswordField label="Confirm new password" name="confirmPassword" autoComplete="new-password" minLength={12} visibilityLabel="confirmed password" required />
          {error && <div className="form-error" role="alert">{error}</div>}
          <Button variant="primary" type="submit" disabled={busy}>{busy ? 'Updating…' : 'Update password'}</Button>
        </form>}
        <a className="auth-link" href="/login">Back to sign in</a>
      </>}
    </section>
  </main>
}
