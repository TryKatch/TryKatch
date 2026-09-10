import { customFetch } from '@trykatch/api-client'
import { Button, PasswordField } from '@trykatch/ui'
import { useState, type FormEvent } from 'react'
import { ProductLogo } from '../components/ProductLogo'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'
import { useI18n } from '../i18n/I18nProvider'

export function ResetPasswordPage() {
  const { t } = useI18n()
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
      setError(reason instanceof Error ? reason.message : t('Your password could not be reset.'))
    } finally {
      setBusy(false)
    }
  }

  return <main className="auth-page">
    <div className="auth-language"><LanguageSwitcher compact /></div>
    <a className="auth-brand" href="/login" aria-label={`Trykatch · ${t('Sign in')}`}><span className="brand-mark"><ProductLogo size={17} /></span><strong>Trykatch</strong></a>
    <section className="auth-card">
      {complete ? <div className="recovery-complete" role="status">
        <h1>{t('Password updated')}</h1>
        <p>{t('Your previous password and active sessions are no longer valid.')}</p>
        <Button type="button" variant="primary" onClick={() => window.location.assign('/login')}>{t('Return to sign in')}</Button>
      </div> : <>
        <h1>{t('Choose a new password')}</h1>
        <p>{t('Use at least 12 characters and avoid a password used elsewhere.')}</p>
        {!email || !token ? <div className="form-error" role="alert">{t('This password reset link is incomplete. Request a new link from the sign-in page.')}</div> : <form onSubmit={submit}>
          <label className="sr-only">{t('Email address')}<input name="email" type="email" autoComplete="username" value={email} readOnly /></label>
          <PasswordField label={t('New password')} name="password" autoComplete="new-password" minLength={12} visibilityLabel={t('New password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required autoFocus />
          <PasswordField label={t('Confirm new password')} name="confirmPassword" autoComplete="new-password" minLength={12} visibilityLabel={t('Confirm new password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />
          {error && <div className="form-error" role="alert">{error}</div>}
          <Button variant="primary" type="submit" disabled={busy}>{t(busy ? 'Updating…' : 'Update password')}</Button>
        </form>}
        <a className="auth-link" href="/login">{t('Back to sign in')}</a>
      </>}
    </section>
  </main>
}
