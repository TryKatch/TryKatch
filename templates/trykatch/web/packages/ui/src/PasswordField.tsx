import { useId, useState, type InputHTMLAttributes, type ReactNode } from 'react'
import { cn } from './primitives'

export interface PasswordFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id' | 'type'> {
  inputId?: string
  label: ReactNode
  visibilityLabel?: string
  showLabel?: string
  hideLabel?: string
}

export function PasswordField({ className, inputId, label, required, visibilityLabel = 'password', showLabel = 'Show', hideLabel = 'Hide', ...props }: PasswordFieldProps) {
  const generatedId = useId()
  const id = inputId ?? generatedId
  const [visible, setVisible] = useState(false)
  const action = visible ? hideLabel : showLabel

  return <div className="password-field">
    <label htmlFor={id}>{label}{required && <> <span aria-hidden="true">*</span></>}</label>
    <span className="password-input">
      <input {...props} id={id} className={cn('password-input-control', className)} type={visible ? 'text' : 'password'} required={required} />
      <button
        className="password-visibility"
        type="button"
        aria-label={`${action} ${visibilityLabel}`}
        aria-pressed={visible}
        onClick={() => setVisible((current) => !current)}
      >
        {visible ? <EyeOffIcon /> : <EyeIcon />}
      </button>
    </span>
  </div>
}

function EyeIcon() {
  return <svg aria-hidden="true" width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    <path d="M2.7 12s3.4-6 9.3-6 9.3 6 9.3 6-3.4 6-9.3 6-9.3-6-9.3-6Z" />
    <circle cx="12" cy="12" r="2.8" />
  </svg>
}

function EyeOffIcon() {
  return <svg aria-hidden="true" width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    <path d="m3 3 18 18" />
    <path d="M10.6 6.2A9.7 9.7 0 0 1 12 6c5.9 0 9.3 6 9.3 6a15.8 15.8 0 0 1-2.3 3.1M6.6 6.7A15.5 15.5 0 0 0 2.7 12s3.4 6 9.3 6a9.2 9.2 0 0 0 3-.5" />
    <path d="M10 10a2.8 2.8 0 0 0 4 4" />
  </svg>
}
