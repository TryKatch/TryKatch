import { Fragment, useEffect, useId, useMemo, useRef, useState, type CSSProperties, type FormEvent, type InputHTMLAttributes, type ReactNode } from 'react'
import { Button, Dialog } from './primitives'

export function PageHeader({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description?: string; actions?: ReactNode }) {
  return <header className="page-header"><div>{eyebrow && <div className="eyebrow">{eyebrow}</div>}<h1>{title}</h1>{description && <p>{description}</p>}</div><div className="page-actions">{actions}</div></header>
}

export function FilterBar({ placeholder = 'Filter…', children, ...inputProps }: InputHTMLAttributes<HTMLInputElement> & { children?: ReactNode }) {
  const inputId = useId()
  return <div className="filter-bar"><label className="sr-only" htmlFor={inputId}>Filter collection</label><input {...inputProps} id={inputId} type="search" placeholder={placeholder} /><div className="filter-actions">{children}</div></div>
}

export function DirtyFormBar({ visible, onSave, onDiscard }: { visible: boolean; onSave(): void; onDiscard(): void }) {
  if (!visible) return null
  return <div className="dirty-bar" role="status"><span>Unsaved changes</span><div><Button variant="ghost" onClick={onDiscard}>Discard</Button><Button variant="primary" onClick={onSave}>Save changes</Button></div></div>
}

export type RowActionIcon = 'view' | 'edit' | 'archive' | 'restore' | 'revoke' | 'delete'

export interface RowAction {
  label: string
  icon: RowActionIcon
  onSelect(): void
  disabled?: boolean
  danger?: boolean
}

function ActionIcon({ icon }: { icon: RowActionIcon }) {
  const paths: Record<RowActionIcon, ReactNode> = {
    view: <><path d="M2.5 10s2.7-5 7.5-5 7.5 5 7.5 5-2.7 5-7.5 5-7.5-5-7.5-5Z" /><circle cx="10" cy="10" r="2" /></>,
    edit: <><path d="m12.8 3.2 4 4L7 17H3v-4Z" /><path d="m10.5 5.5 4 4" /></>,
    archive: <><path d="M3 5h14v3H3zM4.5 8v8h11V8M8 11h4" /></>,
    restore: <><path d="M4.2 7.3A6.5 6.5 0 1 1 4 13" /><path d="M4.2 3.8v3.5h3.5" /></>,
    revoke: <><circle cx="10" cy="10" r="6.5" /><path d="m5.4 5.4 9.2 9.2" /></>,
    delete: <><path d="M3.5 5.5h13M8 3.5h4M5.5 5.5l.8 11h7.4l.8-11M8 8.5v5M12 8.5v5" /></>,
  }
  return <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">{paths[icon]}</svg>
}

export function RowActions({ label, actions }: { label: string; actions: readonly RowAction[] }) {
  const [open, setOpen] = useState(false)
  const container = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (event: PointerEvent) => { if (!container.current?.contains(event.target as Node)) setOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    window.addEventListener('pointerdown', close)
    window.addEventListener('keydown', escape)
    return () => { window.removeEventListener('pointerdown', close); window.removeEventListener('keydown', escape) }
  }, [open])
  return <div className="row-actions" ref={container}>
    <button type="button" className="row-actions-trigger" aria-label={label} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
      <svg data-icon="kebab" viewBox="0 0 20 20" width="17" height="17" aria-hidden="true" fill="currentColor"><circle cx="10" cy="4.25" r="1.35" /><circle cx="10" cy="10" r="1.35" /><circle cx="10" cy="15.75" r="1.35" /></svg>
    </button>
    {open && <div className="row-actions-menu" role="menu" aria-label={label}>{actions.map((action) => <button key={`${action.icon}-${action.label}`} type="button" role="menuitem" className={action.danger ? 'danger' : undefined} disabled={action.disabled} onClick={() => { setOpen(false); action.onSelect() }}><ActionIcon icon={action.icon} /><span>{action.label}</span></button>)}</div>}
  </div>
}

export function DeleteConfirmationDialog({
  open,
  recordName,
  recordType,
  isDeleting = false,
  error,
  onOpenChange,
  onConfirm,
}: {
  open: boolean
  recordName: string
  recordType: string
  isDeleting?: boolean
  error?: string
  onOpenChange(open: boolean): void
  onConfirm(reason: string): void
}) {
  const [reason, setReason] = useState('')
  useEffect(() => { if (!open) setReason('') }, [open])
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (reason.trim().length >= 10) onConfirm(reason.trim())
  }
  return <Dialog open={open} onOpenChange={onOpenChange} title={`Delete ${recordType}`} description="Request deletion while keeping the record recoverable in Archive.">
    <form className="dialog-form delete-confirmation" onSubmit={submit}>
      <div className="delete-record-summary"><span>{recordType}</span><strong>{recordName}</strong></div>
      <label>Reason for deletion <span aria-hidden="true">*</span><textarea value={reason} onChange={(event) => setReason(event.target.value)} minLength={10} maxLength={500} rows={4} required autoFocus placeholder="Explain why this record is being deleted…" /><small>{reason.trim().length}/500 · minimum 10 characters</small></label>
      <p className="delete-accountability">The record moves to Pending deletion. The reason is stored with it and written to the immutable audit trail; authorized users can still restore it.</p>
      {error && <div className="form-error" role="alert">{error}</div>}
      <div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button><Button type="submit" variant="danger" disabled={isDeleting || reason.trim().length < 10}>{isDeleting ? 'Requesting…' : 'Request deletion'}</Button></div>
    </form>
  </Dialog>
}

export type DataTableDensity = 'compact' | 'comfortable' | 'spacious'
export type DataTableSortDirection = 'asc' | 'desc'
export type DataTableValue = string | number | Date | null | undefined

export interface DataTableColumn<T> {
  id: string
  header: string
  cell(row: T): ReactNode
  sortValue?(row: T): DataTableValue
  searchValue?(row: T): string
  hideable?: boolean
  defaultVisible?: boolean
  width?: CSSProperties['width']
  align?: 'left' | 'right'
}

export interface DataTableProps<T> {
  ariaLabel: string
  data: readonly T[]
  columns: readonly DataTableColumn<T>[]
  getRowId(row: T): string
  searchable?: boolean
  searchPlaceholder?: string
  initialSort?: { id: string; direction: DataTableSortDirection }
  initialDensity?: DataTableDensity
  toolbar?: ReactNode
  empty?: ReactNode
  pageSize?: number
  renderExpandedRow?(row: T): ReactNode
  getRowExpansionLabel?(row: T): string
}

function compareValues(left: DataTableValue, right: DataTableValue) {
  if (left == null) return right == null ? 0 : 1
  if (right == null) return -1
  const leftValue = left instanceof Date ? left.getTime() : left
  const rightValue = right instanceof Date ? right.getTime() : right
  if (typeof leftValue === 'number' && typeof rightValue === 'number') return leftValue - rightValue
  return String(leftValue).localeCompare(String(rightValue), undefined, { numeric: true, sensitivity: 'base' })
}

export function DataTable<T>({
  ariaLabel,
  data,
  columns,
  getRowId,
  searchable = true,
  searchPlaceholder = 'Search…',
  initialSort,
  initialDensity = 'comfortable',
  toolbar,
  empty,
  pageSize,
  renderExpandedRow,
  getRowExpansionLabel,
}: DataTableProps<T>) {
  const searchId = useId()
  const columnSchemaKey = `${ariaLabel}|${columns.map((column) => `${column.id}:${column.hideable === false ? 'required' : column.defaultVisible === false ? 'hidden' : 'visible'}`).join('|')}`
  const defaultVisibleColumns = useMemo(() => new Set(columns.filter((column) => column.hideable === false || column.defaultVisible !== false).map((column) => column.id)), [columnSchemaKey])
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState(initialSort)
  const [density, setDensity] = useState<DataTableDensity>(initialDensity)
  const [columnVisibility, setColumnVisibility] = useState(() => ({ schemaKey: columnSchemaKey, columns: defaultVisibleColumns }))
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [expandedRowId, setExpandedRowId] = useState<string>()
  const [page, setPage] = useState(1)
  const settingsMenu = useRef<HTMLDivElement>(null)
  const visibleColumns = columnVisibility.schemaKey === columnSchemaKey ? columnVisibility.columns : defaultVisibleColumns

  useEffect(() => {
    if (columnVisibility.schemaKey === columnSchemaKey) return
    setColumnVisibility({ schemaKey: columnSchemaKey, columns: defaultVisibleColumns })
    setSearch('')
    setSort(initialSort)
    setDensity(initialDensity)
    setSettingsOpen(false)
    setExpandedRowId(undefined)
    setPage(1)
  }, [columnSchemaKey, columnVisibility.schemaKey, defaultVisibleColumns, initialDensity, initialSort])

  useEffect(() => {
    if (!settingsOpen) return
    const closeOnPointerDown = (event: PointerEvent) => {
      if (!settingsMenu.current?.contains(event.target as Node)) setSettingsOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setSettingsOpen(false)
    }
    window.addEventListener('pointerdown', closeOnPointerDown)
    window.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('pointerdown', closeOnPointerDown)
      window.removeEventListener('keydown', closeOnEscape)
    }
  }, [settingsOpen])

  const displayedColumns = columns.filter((column) => visibleColumns.has(column.id))
  const rows = useMemo(() => {
    const needle = search.trim().toLocaleLowerCase()
    const filtered = needle
      ? data.filter((row) => columns.some((column) => (column.searchValue?.(row) ?? String(column.sortValue?.(row) ?? '')).toLocaleLowerCase().includes(needle)))
      : [...data]
    const sortColumn = sort ? columns.find((column) => column.id === sort.id) : undefined
    if (!sort || !sortColumn?.sortValue) return filtered
    return filtered
      .map((row, index) => ({ row, index }))
      .sort((left, right) => {
        const comparison = compareValues(sortColumn.sortValue?.(left.row), sortColumn.sortValue?.(right.row))
        return (sort.direction === 'asc' ? comparison : -comparison) || left.index - right.index
      })
      .map(({ row }) => row)
  }, [columns, data, search, sort])
  const normalizedPageSize = pageSize && pageSize > 0 ? Math.floor(pageSize) : undefined
  const pageCount = normalizedPageSize ? Math.max(1, Math.ceil(rows.length / normalizedPageSize)) : 1
  const visiblePage = Math.min(page, pageCount)
  const visibleRows = normalizedPageSize
    ? rows.slice((visiblePage - 1) * normalizedPageSize, visiblePage * normalizedPageSize)
    : rows

  useEffect(() => {
    if (page <= pageCount) return
    setPage(pageCount)
    setExpandedRowId(undefined)
  }, [page, pageCount])

  function changeSort(column: DataTableColumn<T>) {
    if (!column.sortValue) return
    setPage(1)
    setExpandedRowId(undefined)
    setSort((current) => current?.id === column.id
      ? { id: column.id, direction: current.direction === 'asc' ? 'desc' : 'asc' }
      : { id: column.id, direction: 'asc' })
  }

  function toggleColumn(column: DataTableColumn<T>) {
    if (column.hideable === false) return
    const next = new Set(visibleColumns)
    if (next.has(column.id)) {
      if (next.size > 1) next.delete(column.id)
    } else next.add(column.id)
    setColumnVisibility({ schemaKey: columnSchemaKey, columns: next })
  }

  function toggleRow(row: T) {
    const rowId = getRowId(row)
    setExpandedRowId((current) => current === rowId ? undefined : rowId)
  }

  return <div className={`data-table data-table-${density}`}>
    <div className="data-table-toolbar">
      {searchable && <label className="data-table-search" htmlFor={searchId}><span aria-hidden="true">⌕</span><span className="sr-only">Search table</span><input id={searchId} type="search" value={search} placeholder={searchPlaceholder} onChange={(event) => { setSearch(event.target.value); setPage(1); setExpandedRowId(undefined) }} /></label>}
      {toolbar && <div className="data-table-filters">{toolbar}</div>}
      <span className="data-table-count" aria-live="polite">{rows.length} {rows.length === 1 ? 'result' : 'results'}</span>
      <div className="data-table-menu" ref={settingsMenu}>
        <button className="data-table-menu-trigger" type="button" aria-label="Table settings" aria-haspopup="dialog" aria-expanded={settingsOpen} onClick={() => setSettingsOpen((open) => !open)}>
          <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true"><path d="M4 6h10M18 6h2M4 12h2m4 0h10M4 18h7m4 0h5M14 4v4M8 10v4m5 2v4" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" /></svg>
          Columns
        </button>
        {settingsOpen && <div className="data-table-menu-panel" role="dialog" aria-label="Table settings">
          <div className="data-table-menu-heading"><strong>Table settings</strong><button type="button" aria-label="Close table settings" onClick={() => setSettingsOpen(false)}>×</button></div>
          <fieldset><legend>Row density</legend><div className="density-options">{(['compact', 'comfortable', 'spacious'] as const).map((value) => <label key={value} className={density === value ? 'selected' : ''}><input type="radio" name={`${searchId}-density`} checked={density === value} onChange={() => setDensity(value)} /><span>{value[0].toUpperCase() + value.slice(1)}</span></label>)}</div></fieldset>
          <fieldset><legend>Columns</legend><div className="column-options">{columns.map((column) => {
            const required = column.hideable === false
            return <label key={column.id} className={required ? 'is-required' : ''}><input type="checkbox" aria-label={`${column.header}${required ? ' (required)' : ''}`} checked={visibleColumns.has(column.id)} disabled={required} onChange={() => toggleColumn(column)} /><span>{column.header}</span>{required && <small>Required</small>}</label>
          })}</div></fieldset>
        </div>}
      </div>
    </div>
    <div className="table-wrap"><table aria-label={ariaLabel}><thead><tr>{renderExpandedRow && <th className="data-table-disclosure-heading"><span className="sr-only">Details</span></th>}{displayedColumns.map((column) => <th key={column.id} style={{ width: column.width }} className={column.align === 'right' ? 'is-right' : undefined} aria-sort={sort?.id === column.id ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined}>{column.sortValue ? <button type="button" onClick={() => changeSort(column)}>{column.header}<span aria-hidden="true">{sort?.id === column.id ? (sort.direction === 'asc' ? ' ↑' : ' ↓') : ' ↕'}</span></button> : column.header}</th>)}</tr></thead><tbody>{visibleRows.map((row) => {
      const rowId = getRowId(row)
      const isExpanded = expandedRowId === rowId
      const expansionId = `${searchId}-${rowId}-details`
      const rowLabel = getRowExpansionLabel?.(row) ?? 'row'
      return <Fragment key={rowId}>
        <tr className={isExpanded ? 'is-expanded' : undefined}>
          {renderExpandedRow && <td className="data-table-disclosure-cell"><button type="button" className="data-table-disclosure" aria-label={`${isExpanded ? 'Hide' : 'Show'} details for ${rowLabel}`} aria-expanded={isExpanded} aria-controls={expansionId} onClick={() => toggleRow(row)}><svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><path d="m7 5 5 5-5 5" /></svg></button></td>}
          {displayedColumns.map((column) => <td key={column.id} className={column.align === 'right' ? 'is-right' : undefined}>{column.cell(row)}</td>)}
        </tr>
        {renderExpandedRow && isExpanded && <tr className="data-table-expanded-row"><td id={expansionId} colSpan={displayedColumns.length + 1}>{renderExpandedRow(row)}</td></tr>}
      </Fragment>
    })}</tbody></table></div>
    {rows.length === 0 && (empty ?? <div className="data-table-empty">No matching results.</div>)}
    {normalizedPageSize && rows.length > normalizedPageSize && <div className="table-pagination data-table-pagination"><span>Showing {(visiblePage - 1) * normalizedPageSize + 1}–{Math.min(visiblePage * normalizedPageSize, rows.length)} of {rows.length}</span><div><Button variant="secondary" disabled={visiblePage <= 1} onClick={() => { setPage((value) => Math.max(1, value - 1)); setExpandedRowId(undefined) }}>Previous</Button><span>Page {visiblePage} of {pageCount}</span><Button variant="secondary" disabled={visiblePage >= pageCount} onClick={() => { setPage((value) => Math.min(pageCount, value + 1)); setExpandedRowId(undefined) }}>Next</Button></div></div>}
  </div>
}
