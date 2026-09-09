import { Check, ChevronDown, Languages, Monitor, Moon, Settings2, Sun } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useI18n } from '../i18n/I18nProvider'
import { applyAppearance, defaultShellColor, type Theme } from '../shell/appearance'

const themes = [
  ['light', Sun, 'Light'],
  ['system', Monitor, 'System'],
  ['dark', Moon, 'Dark'],
] as const

const languages = [
  ['en', 'English'],
  ['fr', 'French'],
] as const

function storedTheme(): Theme {
  const value = localStorage.getItem('trykatch-theme')
  return value === 'light' || value === 'dark' || value === 'custom' || value === 'system' ? value : 'system'
}

function storedShellColor() {
  const value = localStorage.getItem('trykatch-shell-color')
  return value && /^#[0-9a-f]{6}$/i.test(value) ? value : defaultShellColor
}

export function LandingPreferencesMenu() {
  const { locale, setLocale, t } = useI18n()
  const [open, setOpen] = useState(false)
  const [theme, setTheme] = useState<Theme>(storedTheme)
  const container = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)')
    const apply = () => applyAppearance(document.documentElement, theme, storedShellColor(), media.matches)
    apply()
    media.addEventListener('change', apply)
    localStorage.setItem('trykatch-theme', theme)
    return () => media.removeEventListener('change', apply)
  }, [theme])

  useEffect(() => {
    const closeOutside = (event: PointerEvent) => {
      if (container.current && !container.current.contains(event.target as Node)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    window.addEventListener('pointerdown', closeOutside)
    window.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('pointerdown', closeOutside)
      window.removeEventListener('keydown', closeOnEscape)
    }
  }, [])

  return <div className="landing-preferences" ref={container}>
    <button
      className="landing-preferences-trigger"
      type="button"
      aria-label={t('Preferences')}
      aria-haspopup="dialog"
      aria-expanded={open}
      onClick={() => setOpen((value) => !value)}
    >
      <Settings2 size={15} aria-hidden="true" />
      <span>{t('Preferences')}</span>
      <ChevronDown className={open ? 'is-open' : ''} size={13} aria-hidden="true" />
    </button>
    {open && <div className="landing-preferences-panel" role="dialog" aria-label={t('Preferences')}>
      <fieldset>
        <legend>{t('Theme')}</legend>
        {themes.map(([value, Icon, label]) => <button
          key={value}
          type="button"
          className={theme === value ? 'selected' : ''}
          aria-label={`${t(label)} ${t('Theme')}`}
          aria-pressed={theme === value}
          onClick={() => setTheme(value)}
        >
          <Icon size={15} aria-hidden="true" />
          <span>{t(label)}</span>
          {theme === value && <Check size={14} aria-hidden="true" />}
        </button>)}
      </fieldset>
      <fieldset>
        <legend><Languages size={13} aria-hidden="true" />{t('Language')}</legend>
        {languages.map(([value, label]) => <button
          key={value}
          type="button"
          className={locale === value ? 'selected' : ''}
          aria-label={`${t(label)} ${t('Language')}`}
          aria-pressed={locale === value}
          onClick={() => setLocale(value)}
        >
          <span>{t(label)}</span>
          <small>{value.toUpperCase()}</small>
          {locale === value && <Check size={14} aria-hidden="true" />}
        </button>)}
      </fieldset>
    </div>}
  </div>
}
