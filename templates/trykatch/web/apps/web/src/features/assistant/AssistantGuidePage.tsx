import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from '@tanstack/react-router'
import { assistantGuide } from '@trykatch/api-client'
import { PageHeader, Surface } from '@trykatch/ui'
import { useI18n } from '../../i18n/I18nProvider'

export function AssistantGuidePage() {
  const { guideId } = useParams({ strict: false })
  const { t } = useI18n()
  const guide = useQuery({ queryKey: ['assistant-guide', guideId], queryFn: ({ signal }) => assistantGuide(guideId ?? '', { signal }), enabled: Boolean(guideId), retry: false })
  return <><PageHeader title={t(guide.data?.title ?? 'Help guide')} description={t('Approved application documentation. Custom implementations may differ.')} /><Link to="/assistant">{t('Back to AI Help')}</Link>
    {guide.isPending && <p role="status">{t('Loading…')}</p>}
    {guide.isError && <p className="assistant-notice" role="alert">{t('This help guide is unavailable. Check your workspace or choose another guide.')}</p>}
    {guide.data && <Surface className="help-guide"><small>{t('Guide revision')}: {guide.data.revision.slice(0, 12)}</small>{guide.data.sections.map((section) => <section key={section.heading}><h2>{section.heading}</h2>{section.text.split('\n\n').map((paragraph, index) => <p key={index}>{paragraph}</p>)}</section>)}</Surface>}
  </>
}
