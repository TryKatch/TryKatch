import { customFetch, type ForgotPasswordResponse } from '@trykatch/api-client'
import { Button } from '@trykatch/ui'
import { useState, type FormEvent } from 'react'
import { ProductLogo } from '../components/ProductLogo'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'
import { useI18n } from '../i18n/I18nProvider'

export function ForgotPasswordPage() {
  const { t } = useI18n()
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
      setError(reason instanceof Error ? reason.message : t('Password reset could not be requested.'))
    } finally {
      setBusy(false)
    }
  }

  return <main className="auth-page">
    <div className="auth-language"><LanguageSwitcher compact /></div>
    <a className="auth-brand" href="/login" aria-label={`Trykatch · ${t('Sign in')}`}><span className="brand-mark"><ProductLogo size={17} /></span><strong>Trykatch</strong></a>
    <section className="auth-card" aria-labelledby="forgot-password-title">
      {result ? <div className="recovery-result" role="status">
        <h1 id="forgot-password-title">{t('Check your email')}</h1>
        <p>{result.message}</p>
        {result.developmentResetUrl && <Button asChild variant="primary"><a href={result.developmentResetUrl}>{t('Continue in local development')}</a></Button>}
        {!result.deliveryConfigured && !result.developmentResetUrl && <small>{t('Email delivery is not configured. Contact your administrator.')}</small>}
        <a className="auth-link" href="/login">{t('Back to sign in')}</a>
      </div> : <>
        <h1 id="forgot-password-title">{t('Forgot your password?')}</h1>
        <p>{t('Enter your email address and we’ll send you a secure reset link.')}</p>
        <form onSubmit={submit}>
          <label>{t('Email address')}<input name="email" type="email" autoComplete="email" required autoFocus /></label>
          {error && <div className="form-error" role="alert">{error}</div>}
          <Button type="submit" variant="primary" disabled={busy}>{t(busy ? 'Sending…' : 'Send reset link')}</Button>
        </form>
        <a className="auth-link" href="/login">{t('Back to sign in')}</a>
      </>}
    </section>
  </main>
}
