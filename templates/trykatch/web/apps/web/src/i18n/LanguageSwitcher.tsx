import { Languages } from 'lucide-react'
import { useI18n } from './I18nProvider'

export function LanguageSwitcher({ compact = false }: { compact?: boolean }) {
  const { locale, setLocale, t } = useI18n()
  return <div className={`language-switcher${compact ? ' is-compact' : ''}`} role="group" aria-label={t('Language')}>
    {!compact && <span><Languages size={15} /><strong>{t('Language')}</strong></span>}
    <div>
      <button type="button" aria-pressed={locale === 'en'} className={locale === 'en' ? 'selected' : ''} onClick={() => setLocale('en')}>EN</button>
      <button type="button" aria-pressed={locale === 'fr'} className={locale === 'fr' ? 'selected' : ''} onClick={() => setLocale('fr')}>FR</button>
    </div>
  </div>
}
