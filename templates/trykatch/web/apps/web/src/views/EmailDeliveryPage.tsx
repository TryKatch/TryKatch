import { useQuery } from '@tanstack/react-query'
import { customFetch, emailDeliveryList, emailTemplatesList, emailTemplatesPreview, type EmailDeliveryView, type EmailTemplatePreview, type EmailTemplateView, type PagedResultOfEmailDeliveryView, type PagedResultOfEmailTemplateView, type SessionResponse } from '@trykatch/api-client'
import { Badge, Button, DataTable, Dialog, EmptyState, FloatingInput, FloatingSelect, PageHeader, type DataTableColumn } from '@trykatch/ui'
import { useMemo, useState } from 'react'
import { useI18n } from '../i18n/I18nProvider'
import { useDataTableLabels } from '../i18n/useDataTableLabels'

const PAGE_SIZE = 50
const ALL = '__all__'
const states = ['queued', 'leased', 'delivered', 'suppressed', 'terminal']

function pageCount(totalCount: number | string) {
  return Math.max(1, Math.ceil(Number(totalCount) / PAGE_SIZE))
}

function pageRange(page: number, totalCount: number | string, t: (message: string, values?: Record<string, string | number>) => string, collection: 'deliveries' | 'templates') {
  const total = Number(totalCount)
  return t(collection === 'deliveries' ? 'Showing {start}–{end} of {total} deliveries' : 'Showing {start}–{end} of {total} templates', {
    start: total ? (page - 1) * PAGE_SIZE + 1 : 0,
    end: Math.min(page * PAGE_SIZE, total),
    total,
  })
}

export function EmailDeliveryPage() {
  const { t, formatDate } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const [boundary, setBoundary] = useState<'organization' | 'identity'>('organization')
  const [state, setState] = useState('')
  const [organizationId, setOrganizationId] = useState('')
  const [deliveryId, setDeliveryId] = useState('')
  const [appliedFilters, setAppliedFilters] = useState({ organizationId: '', deliveryId: '' })
  const [page, setPage] = useState(1)
  const [templatesPage, setTemplatesPage] = useState(1)
  const [selectedTemplate, setSelectedTemplate] = useState<EmailTemplateView>()
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<SessionResponse>('/api/v1/auth/session', { method: 'GET' }), retry: false })
  const canRead = session.data?.platformPermissions?.includes('platform.email.read') === true

  const deliveries = useQuery({
    queryKey: ['platform', 'email-deliveries', boundary, state, appliedFilters.organizationId, appliedFilters.deliveryId, page],
    enabled: canRead,
    retry: false,
    queryFn: () => emailDeliveryList({ boundary, page, pageSize: PAGE_SIZE, state: state || undefined, organizationId: boundary === 'organization' ? appliedFilters.organizationId || undefined : undefined, deliveryId: appliedFilters.deliveryId || undefined }),
  })
  const templates = useQuery({
    queryKey: ['platform', 'email-delivery-templates', templatesPage],
    enabled: canRead,
    retry: false,
    queryFn: () => emailTemplatesList({ page: templatesPage, pageSize: PAGE_SIZE }),
  })
  const preview = useQuery({
    queryKey: ['platform', 'email-template-preview', selectedTemplate?.templateKey, selectedTemplate?.templateVersion],
    enabled: canRead && selectedTemplate !== undefined,
    retry: false,
    queryFn: () => emailTemplatesPreview({ templateKey: selectedTemplate!.templateKey, templateVersion: selectedTemplate!.templateVersion }),
  })

  const deliveryColumns = useMemo<DataTableColumn<EmailDeliveryView>[]>(() => [
    { id: 'deliveryId', header: t('Delivery ID'), hideable: false, cell: (item) => <code className="event-key">{item.deliveryId}</code>, sortValue: (item) => item.deliveryId, searchValue: (item) => `${item.deliveryId} ${item.organizationId ?? ''} ${item.templateKey}` },
    { id: 'boundary', header: t('Boundary'), cell: (item) => item.boundary === 'identity' ? t('Identity') : t('Organization'), sortValue: (item) => item.boundary },
    { id: 'organizationId', header: t('Organization ID'), cell: (item) => item.organizationId ? <code className="event-key">{item.organizationId}</code> : '—', sortValue: (item) => item.organizationId ?? '' },
    { id: 'template', header: t('Template'), cell: (item) => <div><strong>{item.templateKey}</strong><small>{item.templateVersion === null ? '—' : t('Version {version}', { version: item.templateVersion })}</small></div>, sortValue: (item) => item.templateKey },
    { id: 'state', header: t('Status'), cell: (item) => <Badge tone={item.state === 'delivered' ? 'success' : ['suppressed', 'terminal'].includes(item.state) ? 'danger' : item.state === 'leased' ? 'info' : 'neutral'}>{t(item.state)}</Badge>, sortValue: (item) => item.state },
    { id: 'attempts', header: t('Attempts'), align: 'right', cell: (item) => item.attempts, sortValue: (item) => Number(item.attempts) },
    { id: 'createdAt', header: t('Created'), cell: (item) => formatDate(item.createdAtUtc, { dateStyle: 'medium', timeStyle: 'short' }), sortValue: (item) => new Date(item.createdAtUtc) },
    { id: 'nextAttempt', header: t('Next attempt'), defaultVisible: false, cell: (item) => item.nextAttemptAtUtc ? formatDate(item.nextAttemptAtUtc, { dateStyle: 'medium', timeStyle: 'short' }) : '—', sortValue: (item) => item.nextAttemptAtUtc ? new Date(item.nextAttemptAtUtc) : '' },
    { id: 'completedAt', header: t('Completed'), defaultVisible: false, cell: (item) => item.completedAtUtc ? formatDate(item.completedAtUtc, { dateStyle: 'medium', timeStyle: 'short' }) : '—', sortValue: (item) => item.completedAtUtc ? new Date(item.completedAtUtc) : '' },
    { id: 'failureCode', header: t('Failure code'), defaultVisible: false, cell: (item) => item.failureCode ?? '—', sortValue: (item) => item.failureCode ?? '' },
    { id: 'smtpStatusCode', header: t('SMTP status'), defaultVisible: false, align: 'right', cell: (item) => item.smtpStatusCode ?? '—', sortValue: (item) => item.smtpStatusCode ?? 0 },
  ], [formatDate, t])

  const templateColumns = useMemo<DataTableColumn<EmailTemplateView>[]>(() => [
    { id: 'templateKey', header: t('Template'), hideable: false, cell: (item) => <strong>{item.templateKey}</strong>, sortValue: (item) => item.templateKey },
    { id: 'templateVersion', header: t('Version'), cell: (item) => item.templateVersion, sortValue: (item) => Number(item.templateVersion) },
    { id: 'preview', header: t('Actions'), hideable: false, align: 'right', width: 90, cell: (item) => <Button variant="ghost" onClick={() => setSelectedTemplate(item)}>{t('Preview')}</Button> },
  ], [t])

  const totalPages = deliveries.data ? pageCount(deliveries.data.totalCount) : 1
  const templatesTotalPages = templates.data ? pageCount(templates.data.totalCount) : 1
  const previewDialogOpen = selectedTemplate !== undefined

  return <>
    <PageHeader eyebrow={t('Platform administration')} title={t('Email delivery')} description={t('Review queued email work and inspect registered templates.')} />
    {session.isError && <div className="form-error" role="alert">{t('Platform access could not be checked.')}</div>}
    {!session.isError && !session.isLoading && !canRead && <div className="form-error" role="alert">{t('You do not have permission to view email delivery.')}</div>}
    {canRead && <>
      <section aria-labelledby="email-delivery-heading">
        <div className="panel-heading"><div><h2 id="email-delivery-heading">{t('Delivery records')}</h2><p>{t('Read-only delivery status and safe operational metadata.')}</p></div></div>
        <div className="filter-bar">
          <FloatingSelect label={t('Boundary')} value={boundary} onValueChange={(value) => { const next = value as typeof boundary; setBoundary(next); setPage(1); if (next === 'identity') { setOrganizationId(''); setAppliedFilters((current) => ({ ...current, organizationId: '' })) } }} options={[{ value: 'organization', label: t('Organization') }, { value: 'identity', label: t('Identity') }]} />
          <FloatingSelect label={t('Filter by status')} value={state || ALL} onValueChange={(value) => { setState(value === ALL ? '' : value); setPage(1) }} options={[{ value: ALL, label: t('All statuses') }, ...states.map((value) => ({ value, label: t(value) }))]} />
          {boundary === 'organization' && <FloatingInput label={t('Organization ID')} value={organizationId} onChange={(event) => setOrganizationId(event.target.value)} />}
          <FloatingInput label={t('Delivery ID')} value={deliveryId} onChange={(event) => setDeliveryId(event.target.value)} />
          <Button variant="secondary" onClick={() => { setAppliedFilters({ organizationId: organizationId.trim(), deliveryId: deliveryId.trim() }); setPage(1) }}>{t('Apply filters')}</Button>
        </div>
        {deliveries.isError && <div className="form-error" role="alert">{t('Email delivery records could not be loaded.')} <Button variant="ghost" onClick={() => void deliveries.refetch()}>{t('Retry loading')}</Button></div>}
        {deliveries.isLoading ? <div className="shell-loading" role="status" aria-label={t('Loading email delivery records')}><span className="skeleton" /><span className="skeleton" /><span className="skeleton" /></div> : deliveries.data && <>
          {deliveries.data.items.length === 0 && <h2 className="sr-only">{t('Email delivery records')}</h2>}
          <DataTable labels={dataTableLabels} ariaLabel={t('Email delivery records')} data={deliveries.data.items} columns={deliveryColumns} getRowId={(item) => item.deliveryId} searchPlaceholder={dataTableLabels.searchTable} initialSort={{ id: 'createdAt', direction: 'desc' }} empty={<EmptyState title={t('No email delivery records')} description={t('Delivery records matching these filters will appear here.')} />} />
          <div className="table-pagination"><span>{pageRange(page, deliveries.data.totalCount, t, 'deliveries')}</span><div><Button variant="secondary" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>{t('Previous')}</Button><span>{t('Page {page} of {count}', { page, count: totalPages })}</span><Button variant="secondary" disabled={page >= totalPages} onClick={() => setPage((value) => value + 1)}>{t('Next')}</Button></div></div>
        </>}
      </section>
      <section aria-labelledby="email-templates-heading">
        <div className="panel-heading"><div><h2 id="email-templates-heading">{t('Email templates')}</h2><p>{t('Preview registered templates using synthetic content.')}</p></div></div>
        {templates.isError && <div className="form-error" role="alert">{t('Email templates could not be loaded.')} <Button variant="ghost" onClick={() => void templates.refetch()}>{t('Retry loading')}</Button></div>}
        {templates.isLoading ? <div className="shell-loading" role="status" aria-label={t('Loading email templates')}><span className="skeleton" /><span className="skeleton" /></div> : templates.data && <>
          {templates.data.items.length === 0 && <h2 className="sr-only">{t('Email templates')}</h2>}
          <DataTable labels={dataTableLabels} ariaLabel={t('Email templates')} data={templates.data.items} columns={templateColumns} getRowId={(item) => `${item.templateKey}:${item.templateVersion}`} searchPlaceholder={dataTableLabels.searchTable} empty={<EmptyState title={t('No email templates')} description={t('Registered email templates will appear here.')} />} />
          <div className="table-pagination"><span>{pageRange(templatesPage, templates.data.totalCount, t, 'templates')}</span><div><Button variant="secondary" disabled={templatesPage <= 1} onClick={() => setTemplatesPage((value) => value - 1)}>{t('Previous')}</Button><span>{t('Page {page} of {count}', { page: templatesPage, count: templatesTotalPages })}</span><Button variant="secondary" disabled={templatesPage >= templatesTotalPages} onClick={() => setTemplatesPage((value) => value + 1)}>{t('Next')}</Button></div></div>
        </>}
      </section>
    </>}
    <Dialog open={canRead && previewDialogOpen} onOpenChange={(open) => { if (!open) { setSelectedTemplate(undefined) } }} title={t('Synthetic template preview')} description={selectedTemplate ? `${selectedTemplate.templateKey} · ${t('Version {version}', { version: selectedTemplate.templateVersion })}` : undefined}>
      {preview.isError && <div className="form-error" role="alert">{t('Template preview could not be loaded.')} <Button variant="ghost" onClick={() => void preview.refetch()}>{t('Retry loading')}</Button></div>}
      {preview.isLoading && <div className="shell-loading" role="status" aria-label={t('Loading template preview')}><span className="skeleton" /></div>}
      {preview.data && <div className="token-result"><p role="status">{t('This is synthetic preview content, not a real message or delivery record.')}</p><div><strong>{t('Subject')}</strong><code>{preview.data.subject}</code></div><div><strong>{t('Text body')}</strong><code>{preview.data.textBody}</code></div></div>}
    </Dialog>
  </>
}
