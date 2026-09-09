import { cleanup, render, screen } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, describe, expect, it } from 'vitest'
import { LandingPage } from './LandingPage'

describe('LandingPage', () => {
  afterEach(cleanup)

  it('explains the product and provides clear open-source adoption paths', async () => {
    const { container } = render(<LandingPage />)

    expect(screen.getByRole('heading', { name: /ship the product/i })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /the problem/i })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /structured for change/i })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /create your application/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /view source/i })).toHaveAttribute('href', 'https://github.com/Dotnet-Community-DRC/Trykatch')
    expect(screen.getByText(/dotnet new trykatch -n horizon/i)).toBeInTheDocument()

    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
