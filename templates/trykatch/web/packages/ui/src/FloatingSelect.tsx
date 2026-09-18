import * as Select from '@radix-ui/react-select'
import { useId } from 'react'
import { FloatingFieldOutline } from './FloatingField'

export interface FloatingSelectProps {
  label: string
  value: string
  onValueChange: (value: string) => void
  options: readonly { value: string; label: string }[]
  id?: string
  name?: string
  disabled?: boolean
  required?: boolean
  description?: string
  error?: string
}

/** A compact, keyboard-accessible dropdown using the same outline as our text fields. */
export function FloatingSelect({ label, value, onValueChange, options, id: suppliedId, name, disabled, required, description, error }: FloatingSelectProps) {
  const generatedId = useId()
  const id = suppliedId ?? generatedId
  const details = [description && `${id}-help`, error && `${id}-error`].filter(Boolean).join(' ') || undefined
  return <div className="floating-field">
    <Select.Root value={value} onValueChange={onValueChange} name={name} disabled={disabled} required={required}>
      <div className="floating-control floating-control-always">
        <Select.Trigger id={id} className="floating-control-input floating-select-trigger" aria-labelledby={`${id}-label`} aria-describedby={details} aria-invalid={error ? true : undefined}>
          <Select.Value />
          <Select.Icon><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" aria-hidden="true"><path d="m6 9 6 6 6-6" /></svg></Select.Icon>
        </Select.Trigger>
        <label id={`${id}-label`} htmlFor={id}>{label}{required && <span aria-hidden="true"> *</span>}</label>
        <FloatingFieldOutline label={label} required={required} />
      </div>
      <Select.Portal><Select.Content className="floating-select-menu" position="popper" sideOffset={6} align="start" collisionPadding={16}>
        <Select.Viewport>{options.map(option => <Select.Item className="floating-select-option" key={option.value} value={option.value}>
          <Select.ItemText>{option.label}</Select.ItemText>
          <Select.ItemIndicator><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="m5 12 4 4L19 6" /></svg></Select.ItemIndicator>
        </Select.Item>)}</Select.Viewport>
      </Select.Content></Select.Portal>
    </Select.Root>
    {description && <small id={`${id}-help`} className="floating-field-help">{description}</small>}
    {error && <p id={`${id}-error`} className="floating-field-error" role="alert">{error}</p>}
  </div>
}
