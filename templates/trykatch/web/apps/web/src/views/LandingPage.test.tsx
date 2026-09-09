import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LandingPage } from './LandingPage'

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

    fireEvent.click(screen.getByRole('button', { name: 'Preferences' }))
    expect(screen.getByRole('button', { name: 'Light Theme' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'System Theme' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Dark Theme' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Dark Theme' }))

    await waitFor(() => expect(document.documentElement).toHaveAttribute('data-theme', 'dark'))
    expect(localStorage.getItem('trykatch-theme')).toBe('dark')
  })
})
