import { cleanup, render, screen } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, describe, expect, it } from 'vitest'
import { LoginPage } from './LoginPage'

describe('LoginPage', () => {
  afterEach(cleanup)

  it('has an accessible form', async () => {
    const { container } = render(<LoginPage />)
    expect(screen.getByRole('heading', { name: /^sign in$/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /forgot password/i })).toHaveAttribute('href', '/forgot-password')
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
