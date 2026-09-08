import { fireEvent, render, screen } from '@testing-library/react'
import axe from 'axe-core'
import { describe, expect, it, vi } from 'vitest'
import { SignOutDialog } from './SignOutDialog'

describe('SignOutDialog', () => {
  it('asks for confirmation before ending the session', async () => {
    const confirm = vi.fn()
    const { container } = render(<SignOutDialog open identity="Ada Lovelace" isPending={false} onOpenChange={() => undefined} onConfirm={confirm} />)
    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    expect(confirm).toHaveBeenCalledOnce()
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
