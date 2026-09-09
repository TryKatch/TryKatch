import { Button, Dialog } from '@trykatchapp/ui'
import { LogOut } from 'lucide-react'
import { useI18n } from '../i18n/I18nProvider'

export function SignOutDialog({
  open,
  identity,
  isPending,
  error,
  onOpenChange,
  onConfirm,
}: {
  open: boolean
  identity: string
  isPending: boolean
  error?: string
  onOpenChange(open: boolean): void
  onConfirm(): void
}) {
  const { t } = useI18n()
  return <Dialog
    open={open}
    onOpenChange={(nextOpen) => { if (!isPending) onOpenChange(nextOpen) }}
    title={t('Sign out?')}
    description={t('Your session on this device will end securely.')}
    className="confirmation-dialog"
  >
    <div className="confirmation-summary">
      <span className="confirmation-icon" aria-hidden="true"><LogOut size={18} /></span>
      <div><strong>{identity}</strong><span>{t('You can sign back in at any time.')}</span></div>
    </div>
    {error && <div className="form-error" role="alert">{error}</div>}
    <div className="dialog-actions">
      <Button type="button" variant="ghost" disabled={isPending} onClick={() => onOpenChange(false)}>{t('Stay signed in')}</Button>
      <Button type="button" variant="primary" disabled={isPending} onClick={onConfirm}>{t(isPending ? 'Signing out…' : 'Sign out')}</Button>
    </div>
  </Dialog>
}
