import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import * as DialogPrimitive from '@radix-ui/react-dialog'
import { FloatingInput, type FloatingInputProps } from './FloatingField'

export interface SearchFieldProps extends Omit<FloatingInputProps, 'type' | 'leadingIcon' | 'trailingAction'> {
  filters?: ReactNode
  filterLabel?: string
  closeFiltersLabel?: string
}

export function SearchField({ filters, filterLabel = 'Filters', closeFiltersLabel = 'Close filters', ...props }: SearchFieldProps) {
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState<CSSProperties>({})
  const trigger = useRef<HTMLButtonElement>(null)

  function updatePosition() {
    const element = trigger.current
    const viewport = element?.ownerDocument.defaultView
    if (!element || !viewport) return
    const anchor = element.getBoundingClientRect()
    const width = Math.min(280, viewport.innerWidth - 16)
    const maxHeight = Math.min(340, viewport.innerHeight - 16)
    setPosition({ width, maxHeight, left: Math.max(8, Math.min(anchor.right - width, viewport.innerWidth - width - 8)), top: Math.max(8, Math.min(anchor.bottom + 8, viewport.innerHeight - maxHeight - 8)) })
  }

  useEffect(() => {
    if (!open) return
    const viewport = trigger.current?.ownerDocument.defaultView
    viewport?.addEventListener('resize', updatePosition)
    viewport?.addEventListener('scroll', updatePosition, true)
    return () => {
      viewport?.removeEventListener('resize', updatePosition)
      viewport?.removeEventListener('scroll', updatePosition, true)
    }
  }, [open])

  return <DialogPrimitive.Root modal={false} open={open && Boolean(filters)} onOpenChange={(nextOpen) => { if (nextOpen) updatePosition(); setOpen(nextOpen) }}>
    <FloatingInput {...props} type="search" leadingIcon={<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></svg>} trailingAction={filters && <DialogPrimitive.Trigger asChild><button ref={trigger} className="search-filter-trigger" type="button" aria-label={filterLabel} disabled={props.disabled}>
      <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" aria-hidden="true"><path d="M4 6h16M7 12h10M10 18h4" /></svg>
    </button></DialogPrimitive.Trigger>} />
    {filters && <DialogPrimitive.Portal><DialogPrimitive.Content className="search-filter-popover" style={position} aria-describedby={undefined}>
      <header><DialogPrimitive.Title>{filterLabel}</DialogPrimitive.Title><DialogPrimitive.Close className="search-filter-close" aria-label={closeFiltersLabel}>×</DialogPrimitive.Close></header>
      {filters}
    </DialogPrimitive.Content></DialogPrimitive.Portal>}
  </DialogPrimitive.Root>
}
