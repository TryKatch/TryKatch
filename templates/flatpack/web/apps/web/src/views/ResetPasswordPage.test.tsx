import { render, screen } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, describe, expect, it } from 'vitest'
import { ResetPasswordPage } from './ResetPasswordPage'

describe('ResetPasswordPage', () => {
  afterEach(() => window.history.replaceState({}, '', '/'))

  it('renders an accessible new-password form for a valid link shape', async () => {
    window.history.replaceState({}, '', '/reset-password?email=person%40example.com&token=one-time-token')
    const { container } = render(<ResetPasswordPage />)
    expect(screen.getByRole('heading', { name: /choose a new password/i })).toBeInTheDocument()
    expect(screen.getByLabelText(/^New password/i, { selector: 'input' })).toHaveFocus()
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
