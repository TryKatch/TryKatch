import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, describe, expect, it } from 'vitest'
import { LoginPage } from './LoginPage'

describe('LoginPage', () => {
  afterEach(cleanup)

  it('has an accessible form', async () => {
    const { container } = render(<LoginPage />)
    expect(screen.getByRole('heading', { name: /sign in to your workspace/i })).toBeInTheDocument()
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })

  it('opens a focused password recovery dialog without leaving sign in', async () => {
    render(<LoginPage />)
    fireEvent.click(screen.getByRole('button', { name: /forgot password/i }))
    const dialog = screen.getByRole('dialog', { name: /reset your password/i })
    expect(dialog).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Email address')).toHaveFocus()
    const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
