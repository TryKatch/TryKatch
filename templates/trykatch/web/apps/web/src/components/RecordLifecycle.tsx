import { Badge, Button, Dialog } from '@trykatchapp/ui'
import type { ReactNode } from 'react'
import { useI18n } from '../i18n/I18nProvider'

export type LifecycleScope = 'active' | 'archived' | 'deleted'

export interface RecordLifecycle {
  status: 'Active' | 'Archived' | 'Deleted'
  archivedAt?: string | null
  deletedAt?: string | null
  deletionReason?: string | null
}

export function LifecycleFilter({ value, onChange }: { value: LifecycleScope; onChange(value: LifecycleScope): void }) {
  const { t } = useI18n()
  return <select className="lifecycle-filter" aria-label={t('Record lifecycle')} value={value} onChange={(event) => onChange(event.target.value as LifecycleScope)}>
    <option value="active">{t('Active records')}</option>
    <option value="archived">{t('Archived records')}</option>
    <option value="deleted">{t('Pending deletion')}</option>
  </select>
}

export function LifecycleBadge({ lifecycle }: { lifecycle: RecordLifecycle }) {
  const { t } = useI18n()
  const tone = lifecycle.status === 'Active' ? 'success' : lifecycle.status === 'Archived' ? 'warning' : 'danger'
  return <Badge tone={tone}>{t(lifecycle.status === 'Deleted' ? 'Pending deletion' : lifecycle.status)}</Badge>
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
  const { t } = useI18n()
  const initials = title.split(/\s+/).filter(Boolean).map((part) => part[0]).join('').slice(0, 2).toUpperCase() || 'R'
  return <Dialog open={open} onOpenChange={onOpenChange} title={title} description={description} className="record-details-dialog">
    <div className="record-details-context">
      <span className="record-details-mark" aria-hidden="true">{initials}</span>
      <div><span className="eyebrow">{t(recordType)}</span><strong>{t('Record overview')}</strong><small>{t(contextDescription)}</small></div>
      {status && <div className="record-details-status">{status}</div>}
    </div>
    <section className="record-details-section" aria-labelledby="record-details-heading">
      <div className="record-details-section-heading"><h3 id="record-details-heading">{t('Information')}</h3><span>{details.length} {t(details.length === 1 ? 'field' : 'fields')}</span></div>
      <dl className="record-details">{details.map((detail) => <div key={detail.label}><dt>{t(detail.label)}</dt><dd>{detail.value || '—'}</dd></div>)}</dl>
    </section>
    <footer className="record-details-actions">{actions}<Button type="button" variant="secondary" onClick={() => onOpenChange(false)}>{t('Close')}</Button></footer>
  </Dialog>
}

export function formatRecordDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}
