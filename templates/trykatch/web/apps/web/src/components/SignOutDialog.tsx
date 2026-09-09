import { Button, Dialog } from '@trykatchapp/ui'
import { LogOut } from 'lucide-react'

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
  return <Dialog
    open={open}
    onOpenChange={(nextOpen) => { if (!isPending) onOpenChange(nextOpen) }}
    title="Sign out?"
    description="Your session on this device will end securely."
    className="confirmation-dialog"
  >
    <div className="confirmation-summary">
      <span className="confirmation-icon" aria-hidden="true"><LogOut size={18} /></span>
      <div><strong>{identity}</strong><span>You can sign back in at any time.</span></div>
    </div>
    {error && <div className="form-error" role="alert">{error}</div>}
    <div className="dialog-actions">
      <Button type="button" variant="ghost" disabled={isPending} onClick={() => onOpenChange(false)}>Stay signed in</Button>
      <Button type="button" variant="primary" disabled={isPending} onClick={onConfirm}>{isPending ? 'Signing out…' : 'Sign out'}</Button>
    </div>
  </Dialog>
}
