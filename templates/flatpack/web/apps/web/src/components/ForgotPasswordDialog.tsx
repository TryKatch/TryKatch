import { customFetch, type ForgotPasswordResponse } from '@flatpackapp/api-client'
import { Button, Dialog } from '@flatpackapp/ui'
import { ArrowRight, MailCheck } from 'lucide-react'
import { useEffect, useState, type FormEvent } from 'react'

export function ForgotPasswordDialog({ open, onOpenChange }: { open: boolean; onOpenChange(open: boolean): void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [result, setResult] = useState<ForgotPasswordResponse>()

  useEffect(() => {
    if (!open) {
      setBusy(false)
      setError(undefined)
      setResult(undefined)
    }
  }, [open])

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

  return <Dialog
    open={open}
    onOpenChange={(nextOpen) => { if (!busy) onOpenChange(nextOpen) }}
    title={result ? 'Check your email' : 'Reset your password'}
    description={result ? 'If the account is eligible, the reset instructions are ready.' : 'Enter the email address associated with your account.'}
    className="recovery-dialog"
  >
    {result ? <div className="recovery-result" role="status">
      <span className="recovery-result-icon" aria-hidden="true"><MailCheck size={20} /></span>
      <p>{result.message}</p>
      {result.developmentResetUrl && <Button asChild variant="primary"><a href={result.developmentResetUrl}>Continue in local development <ArrowRight size={15} /></a></Button>}
      {!result.deliveryConfigured && !result.developmentResetUrl && <small>Email delivery is not configured. Contact your administrator.</small>}
      <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>Back to sign in</Button>
    </div> : <form className="dialog-form" onSubmit={submit}>
      <label>Email address<input name="email" type="email" autoComplete="email" required autoFocus /></label>
      <p className="form-guidance">For security, the response is the same whether or not an account exists.</p>
      {error && <div className="form-error" role="alert">{error}</div>}
      <div className="dialog-actions"><Button type="button" variant="ghost" disabled={busy} onClick={() => onOpenChange(false)}>Cancel</Button><Button type="submit" variant="primary" disabled={busy}>{busy ? 'Sending…' : 'Send reset link'}</Button></div>
    </form>}
  </Dialog>
}
