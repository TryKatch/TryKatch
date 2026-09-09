import { Monitor, Moon, Sun } from 'lucide-react'
import { useEffect, useState } from 'react'
import { useI18n } from '../i18n/I18nProvider'
import { applyAppearance, defaultShellColor, type Theme } from '../shell/appearance'

const themes = [
  ['light', Sun, 'Light'],
  ['system', Monitor, 'System'],
  ['dark', Moon, 'Dark'],
] as const

function storedTheme(): Theme {
  const value = localStorage.getItem('trykatch-theme')
  return value === 'light' || value === 'dark' || value === 'custom' || value === 'system' ? value : 'system'
}

function storedShellColor() {
  const value = localStorage.getItem('trykatch-shell-color')
  return value && /^#[0-9a-f]{6}$/i.test(value) ? value : defaultShellColor
}

export function LandingThemeSwitcher() {
  const { t } = useI18n()
  const [theme, setTheme] = useState<Theme>(storedTheme)

  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)')
    const apply = () => applyAppearance(document.documentElement, theme, storedShellColor(), media.matches)
    apply()
    media.addEventListener('change', apply)
    localStorage.setItem('trykatch-theme', theme)
    return () => media.removeEventListener('change', apply)
  }, [theme])

  return <div className="landing-theme-switcher" role="group" aria-label={t('Theme')}>
    {themes.map(([value, Icon, label]) => <button
      key={value}
      type="button"
      className={theme === value ? 'selected' : ''}
      aria-label={`${t(label)} ${t('Theme')}`}
      aria-pressed={theme === value}
      title={t(label)}
      onClick={() => setTheme(value)}
    >
      <Icon size={14} aria-hidden="true" />
      <span>{t(label)}</span>
    </button>)}
  </div>
}
