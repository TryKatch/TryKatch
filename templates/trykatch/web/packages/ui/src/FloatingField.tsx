import { useId, type InputHTMLAttributes, type ReactNode, type Ref, type TextareaHTMLAttributes } from 'react'
import { cn } from './primitives'

interface FieldPresentation {
  label: ReactNode
  error?: string
  description?: string
}

// Decorative outline: the associated label remains the input's only accessible name.
export function FloatingFieldOutline({ label, required }: { label: ReactNode; required?: boolean }) {
  return <fieldset className="floating-outline" aria-hidden="true"><legend><span>{label}{required && ' *'}</span></legend></fieldset>
}

export type FloatingInputProps = FieldPresentation & Omit<InputHTMLAttributes<HTMLInputElement>, 'placeholder' | 'type'> & {
  leadingIcon?: ReactNode
  trailingAction?: ReactNode
  type?: 'text' | 'email' | 'password' | 'search' | 'tel' | 'url' | 'number' | 'date' | 'datetime-local' | 'time'
}

export function FloatingInput({ label, error, description, leadingIcon, trailingAction, id: inputId, className, required, type = 'text', 'aria-describedby': describedBy, ...props }: FloatingInputProps) {
  const generatedId = useId()
  const id = inputId ?? generatedId
  const details = [describedBy, description && `${id}-help`, error && `${id}-error`].filter(Boolean).join(' ') || undefined
  return <div className="floating-field">
    <div className={cn('floating-control', leadingIcon && 'floating-control-with-icon', ['date', 'datetime-local', 'time'].includes(type) && 'floating-control-always')}>
      <input {...props} id={id} type={type} required={required} placeholder=" " className={cn('floating-control-input', className)} aria-invalid={error ? true : props['aria-invalid']} aria-describedby={details} />
      {leadingIcon && <span className="floating-field-icon" aria-hidden="true">{leadingIcon}</span>}
      <label htmlFor={id}>{label}{required && <span aria-hidden="true"> *</span>}</label>
      <FloatingFieldOutline label={label} required={required} />
      {trailingAction && <span className="floating-field-action">{trailingAction}</span>}
    </div>
    {description && <small id={`${id}-help`} className="floating-field-help">{description}</small>}
    {error && <p id={`${id}-error`} className="floating-field-error" role="alert">{error}</p>}
  </div>
}

export type FloatingTextareaProps = FieldPresentation & Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'placeholder'> & {
  ref?: Ref<HTMLTextAreaElement>
}

export function FloatingTextarea({ label, error, description, id: inputId, className, required, 'aria-describedby': describedBy, ...props }: FloatingTextareaProps) {
  const generatedId = useId()
  const id = inputId ?? generatedId
  const details = [describedBy, description && `${id}-help`, error && `${id}-error`].filter(Boolean).join(' ') || undefined
  return <div className="floating-field">
    <div className="floating-control">
      <textarea {...props} id={id} required={required} placeholder=" " className={cn('floating-control-input', className)} aria-invalid={error ? true : props['aria-invalid']} aria-describedby={details} />
      <label htmlFor={id}>{label}{required && <span aria-hidden="true"> *</span>}</label>
      <FloatingFieldOutline label={label} required={required} />
    </div>
    {description && <small id={`${id}-help`} className="floating-field-help">{description}</small>}
    {error && <p id={`${id}-error`} className="floating-field-error" role="alert">{error}</p>}
  </div>
}
