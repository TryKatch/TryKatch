import { render, screen } from '@testing-library/react'
import axe from 'axe-core'
import { describe, expect, it } from 'vitest'
import { ForgotPasswordPage } from './ForgotPasswordPage'

describe('ForgotPasswordPage', () => {
  it('renders an accessible standalone recovery form', async () => {
    const { container } = render(<ForgotPasswordPage />)
    expect(screen.getByRole('heading', { name: /forgot your password/i })).toBeInTheDocument()
    expect(screen.getByLabelText('Email address')).toHaveFocus()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: /back to sign in/i })).toHaveAttribute('href', '/login')
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
