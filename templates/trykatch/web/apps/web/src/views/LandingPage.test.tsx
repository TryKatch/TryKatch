import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LandingPage } from './LandingPage'
import { I18nProvider } from '../i18n/I18nProvider'

describe('LandingPage', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
    vi.stubGlobal('matchMedia', vi.fn().mockReturnValue({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }))
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('explains the product and provides clear open-source adoption paths', async () => {
    const { container } = render(<LandingPage />)

    expect(screen.getByRole('heading', { name: /ship the product/i })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /the problem/i })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /structured for change/i })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /create your application/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /view source/i })).toHaveAttribute('href', 'https://github.com/TryKatch/TryKatch')
    expect(screen.getByText(/dotnet new trykatch -n horizon/i)).toBeInTheDocument()

    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })

  it('offers persistent light, system, and dark appearance modes', async () => {
    render(<LandingPage />)

    fireEvent.click(screen.getByRole('button', { name: 'Theme: System' }))
    expect(screen.getByRole('menuitemradio', { name: 'Light' })).toBeInTheDocument()
    expect(screen.getByRole('menuitemradio', { name: 'System' })).toHaveAttribute('aria-checked', 'true')
    expect(screen.getByRole('menuitemradio', { name: 'Dark' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('menuitemradio', { name: 'Dark' }))

    await waitFor(() => expect(document.documentElement).toHaveAttribute('data-theme', 'dark'))
    expect(localStorage.getItem('trykatch-theme')).toBe('dark')
  })

  it('does not show marketing media in generated applications by default', () => {
    vi.stubEnv('VITE_TRYKATCH_LAUNCH_VIDEO', '')
    const { container } = render(<LandingPage />)
    expect(container.querySelector('video')).toBeNull()
    expect(screen.queryByText('See what you can build')).not.toBeInTheDocument()
  })

  it('provides an accessible, user-controlled overview on the marketing homepage', async () => {
    vi.stubEnv('VITE_TRYKATCH_LAUNCH_VIDEO', 'true')
    const { container } = render(<LandingPage />)
    const video = screen.getByLabelText('Trykatch product overview')
    expect(video).toHaveAttribute('controls')
    expect(video).toHaveAttribute('playsinline')
    expect(video).toHaveAttribute('preload', 'none')
    expect(video).not.toHaveAttribute('autoplay')
    expect(video).not.toHaveAttribute('loop')
    expect(video).toHaveAttribute('poster', '/marketing/trykatch-launch.webp')
    expect(video.querySelector('source')).toHaveAttribute('src', '/marketing/trykatch-launch.mp4')
    expect(screen.getByText(/not a runtime benchmark/i)).toBeInTheDocument()
    fireEvent.click(screen.getByText('Video description and credits'))
    expect(container.querySelector('details')).toHaveAttribute('open')
    expect(screen.getByText(/trykatch module create Invoicing/)).toBeInTheDocument()
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })

  it('translates the video description and credits into French', () => {
    vi.stubEnv('VITE_TRYKATCH_LAUNCH_VIDEO', 'true')
    localStorage.setItem('trykatch-locale', 'fr')
    render(<I18nProvider><LandingPage /></I18nProvider>)
    expect(screen.getByRole('heading', { name: 'Découvrez ce que vous pouvez construire' })).toBeInTheDocument()
    expect(screen.getByLabelText('Présentation du produit Trykatch')).toBeInTheDocument()
    expect(screen.getByText('Description de la vidéo et crédits')).toBeInTheDocument()
  })
})
