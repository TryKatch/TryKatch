import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { I18nProvider, useI18n } from './I18nProvider'
import { LanguageSwitcher } from './LanguageSwitcher'

function Probe() {
  const { t } = useI18n()
  return <><LanguageSwitcher /><h1>{t('Sign in')}</h1></>
}

describe('application localization', () => {
  afterEach(() => {
    localStorage.removeItem('trykatch-locale')
    document.documentElement.lang = 'en'
  })

  it('switches the application to French and persists the preference', () => {
    render(<I18nProvider><Probe /></I18nProvider>)

    fireEvent.click(screen.getByRole('button', { name: 'FR' }))

    expect(screen.getByRole('heading', { name: 'Se connecter' })).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('fr')
    expect(localStorage.getItem('trykatch-locale')).toBe('fr')
  })
})
