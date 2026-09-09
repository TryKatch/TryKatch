import { Check, ChevronDown, Languages, Monitor, Moon, Sun } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useI18n } from '../i18n/I18nProvider'
import { applyAppearance, defaultShellColor, type Theme } from '../shell/appearance'

type LandingTheme = Exclude<Theme, 'custom'>
type OpenMenu = 'language' | 'theme' | null

const themes = [
  ['light', Sun, 'Light'],
  ['system', Monitor, 'System'],
  ['dark', Moon, 'Dark'],
] as const

const languages = [
  ['en', 'English'],
  ['fr', 'French'],
] as const

function storedTheme(): LandingTheme {
  const value = localStorage.getItem('trykatch-theme')
  return value === 'light' || value === 'dark' || value === 'system' ? value : 'system'
}

function storedShellColor() {
  const value = localStorage.getItem('trykatch-shell-color')
  return value && /^#[0-9a-f]{6}$/i.test(value) ? value : defaultShellColor
}

export function LandingPreferencesMenu() {
  const { locale, setLocale, t } = useI18n()
  const [openMenu, setOpenMenu] = useState<OpenMenu>(null)
  const [theme, setTheme] = useState<LandingTheme>(storedTheme)
  const container = useRef<HTMLDivElement>(null)
  const ThemeIcon = themes.find(([value]) => value === theme)?.[1] ?? Monitor
  const themeLabel = themes.find(([value]) => value === theme)?.[2] ?? 'System'
  const languageLabel = locale === 'fr' ? 'French' : 'English'

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
      if (container.current && !container.current.contains(event.target as Node)) setOpenMenu(null)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpenMenu(null)
    }
    window.addEventListener('pointerdown', closeOutside)
    window.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('pointerdown', closeOutside)
      window.removeEventListener('keydown', closeOnEscape)
    }
  }, [])

  const selectTheme = (value: LandingTheme) => {
    setTheme(value)
    setOpenMenu(null)
  }

  const selectLanguage = (value: 'en' | 'fr') => {
    setLocale(value)
    setOpenMenu(null)
  }

  return <div className="landing-preferences" ref={container}>
    <div className="landing-preference-menu">
      <button
        className="landing-preference-trigger"
        type="button"
        aria-label={`${t('Theme')}: ${t(themeLabel)}`}
        aria-haspopup="menu"
        aria-expanded={openMenu === 'theme'}
        onClick={() => setOpenMenu((value) => value === 'theme' ? null : 'theme')}
      >
        <ThemeIcon size={15} aria-hidden="true" />
        <span>{t(themeLabel)}</span>
        <ChevronDown className={openMenu === 'theme' ? 'is-open' : ''} size={13} aria-hidden="true" />
      </button>
      {openMenu === 'theme' && <div className="landing-preference-panel" role="menu" aria-label={t('Theme')}>
        {themes.map(([value, Icon, label]) => <button
          key={value}
          type="button"
          role="menuitemradio"
          aria-checked={theme === value}
          onClick={() => selectTheme(value)}
        >
          <Icon size={15} aria-hidden="true" />
          <span>{t(label)}</span>
          {theme === value && <Check size={14} aria-hidden="true" />}
        </button>)}
      </div>}
    </div>

    <div className="landing-preference-menu">
      <button
        className="landing-preference-trigger"
        type="button"
        aria-label={`${t('Language')}: ${t(languageLabel)}`}
        aria-haspopup="menu"
        aria-expanded={openMenu === 'language'}
        onClick={() => setOpenMenu((value) => value === 'language' ? null : 'language')}
      >
        <Languages size={15} aria-hidden="true" />
        <span>{t(languageLabel)}</span>
        <ChevronDown className={openMenu === 'language' ? 'is-open' : ''} size={13} aria-hidden="true" />
      </button>
      {openMenu === 'language' && <div className="landing-preference-panel landing-language-panel" role="menu" aria-label={t('Language')}>
        {languages.map(([value, label]) => <button
          key={value}
          type="button"
          role="menuitemradio"
          aria-checked={locale === value}
          onClick={() => selectLanguage(value)}
        >
          <span className="landing-language-code">{value.toUpperCase()}</span>
          <span>{t(label)}</span>
          {locale === value && <Check size={14} aria-hidden="true" />}
        </button>)}
      </div>}
    </div>
  </div>
}
