import { Badge, Button, Dialog } from '@flatpackapp/ui'
import type { ReactNode } from 'react'

export type LifecycleScope = 'active' | 'archived' | 'deleted'

export interface RecordLifecycle {
  status: 'Active' | 'Archived' | 'Deleted'
  archivedAt?: string | null
  deletedAt?: string | null
  deletionReason?: string | null
}

export function LifecycleFilter({ value, onChange }: { value: LifecycleScope; onChange(value: LifecycleScope): void }) {
  return <select className="lifecycle-filter" aria-label="Record lifecycle" value={value} onChange={(event) => onChange(event.target.value as LifecycleScope)}>
    <option value="active">Active records</option>
    <option value="archived">Archived records</option>
    <option value="deleted">Pending deletion</option>
  </select>
}

export function LifecycleBadge({ lifecycle }: { lifecycle: RecordLifecycle }) {
  const tone = lifecycle.status === 'Active' ? 'success' : lifecycle.status === 'Archived' ? 'warning' : 'danger'
  return <Badge tone={tone}>{lifecycle.status === 'Deleted' ? 'Pending deletion' : lifecycle.status}</Badge>
}

export interface RecordDetail {
  label: string
  value: ReactNode
}

export function RecordDetailsDialog({
  open,
  title,
  description,
  details,
  recordType = 'Record',
  contextDescription = 'Authoritative values from the current organization.',
  status,
  actions,
  onOpenChange,
}: {
  open: boolean
  title: string
  description?: string
  details: readonly RecordDetail[]
  recordType?: string
  contextDescription?: string
  status?: ReactNode
  actions?: ReactNode
  onOpenChange(open: boolean): void
}) {
  const initials = title.split(/\s+/).filter(Boolean).map((part) => part[0]).join('').slice(0, 2).toUpperCase() || 'R'
  return <Dialog open={open} onOpenChange={onOpenChange} title={title} description={description} className="record-details-dialog">
    <div className="record-details-context">
      <span className="record-details-mark" aria-hidden="true">{initials}</span>
      <div><span className="eyebrow">{recordType}</span><strong>Record overview</strong><small>{contextDescription}</small></div>
      {status && <div className="record-details-status">{status}</div>}
    </div>
    <section className="record-details-section" aria-labelledby="record-details-heading">
      <div className="record-details-section-heading"><h3 id="record-details-heading">Information</h3><span>{details.length} fields</span></div>
      <dl className="record-details">{details.map((detail) => <div key={detail.label}><dt>{detail.label}</dt><dd>{detail.value || '—'}</dd></div>)}</dl>
    </section>
    <footer className="record-details-actions">{actions}<Button type="button" variant="secondary" onClick={() => onOpenChange(false)}>Close</Button></footer>
  </Dialog>
}

export function formatRecordDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}
