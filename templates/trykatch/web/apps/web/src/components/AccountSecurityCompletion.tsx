import { Button, Surface } from '@trykatch/ui'
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { useI18n } from '../i18n/I18nProvider'

const CompletionContext = createContext<((codes: string[]) => void) | null>(null)

export function useAccountSecurityCompletion() {
  const complete = useContext(CompletionContext)
  if (!complete) throw new Error('Account security requires the root completion boundary.')
  return complete
}

// Keep this above every authenticated shell: MFA completion ends the session,
// so shell redirects/refetches must not own the only copy of recovery codes.
// This state is deliberately absent from URLs, history, query caches and storage.
export function AccountSecurityCompletionBoundary({ children }: { children: ReactNode }) {
  const { t } = useI18n()
  const [codes, setCodes] = useState<string[] | null>(null)
  const [copyFailed, setCopyFailed] = useState(false)
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    if (codes !== null) heading.current?.focus()
    if (!codes?.length) return
    const warnBeforeLeaving = (event: BeforeUnloadEvent) => { event.preventDefault() }
    window.addEventListener('beforeunload', warnBeforeLeaving)
    return () => window.removeEventListener('beforeunload', warnBeforeLeaving)
  }, [codes])

  async function copyCodes() {
    if (!codes?.length) return
    try { await navigator.clipboard.writeText(codes.join('\n')) }
    catch { setCopyFailed(true) }
  }

  return <CompletionContext.Provider value={setCodes}>
    {codes === null ? children : <main className="account-security-completion">
      <Surface className="profile-card account-security-card token-result">
        <h1 ref={heading} tabIndex={-1}>{t('Security settings updated')}</h1>
        <p role="status">{t('Your session has ended. Sign in again to continue.')}</p>
        {codes.length > 0 && <>
          <p>{t('Store these recovery codes securely. Each code can be used once. They will not be shown again.')}</p>
          <code>{codes.join('\n')}</code>
          <Button onClick={() => { void copyCodes() }}>{t('Copy recovery codes')}</Button>
          {copyFailed && <div className="form-error" role="alert">{t('Copy failed. Save your recovery codes manually.')}</div>}
        </>}
        {/* Clear the codes on explicit acknowledgement; never remount the old signed-out shell. */}
        <a href="/login" onClick={() => setCodes([])}>{t(codes.length > 0 ? 'I saved my recovery codes — sign in' : 'Sign in')}</a>
      </Surface>
    </main>}
  </CompletionContext.Provider>
}
