import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LoginPage } from './LoginPage'

const api = vi.hoisted(() => ({
  customFetch: vi.fn(),
  setAntiforgeryToken: vi.fn(),
}))

vi.mock('@flatpackapp/api-client', () => api)

describe('LoginPage', () => {
  afterEach(cleanup)

  it('has an accessible form', async () => {
    const { container } = render(<LoginPage navigate={() => undefined} />)
    expect(screen.getByRole('heading', { name: /^sign in$/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /forgot password/i })).toHaveAttribute('href', '/forgot-password')
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })

  it('refreshes request protection after login before selecting a workspace', async () => {
    let antiforgeryRequests = 0
    api.customFetch.mockImplementation(async (url: string) => {
      if (url === '/api/v1/auth/antiforgery') {
        antiforgeryRequests += 1
        return { token: `token-${antiforgeryRequests}` }
      }
      if (url === '/api/v1/auth/login') return { hasPlatformAccess: false, isPlatformAdministrator: false }
      if (url === '/api/v1/me/organizations') return [{ id: 'organization-1' }]
      if (url === '/api/v1/workspace/select') {
        if (antiforgeryRequests < 2) throw new Error('Request verification failed')
        return undefined
      }
      throw new Error(`Unexpected request: ${url}`)
    })
    const navigate = vi.fn()
    render(<LoginPage navigate={navigate} />)

    fireEvent.change(screen.getByRole('textbox', { name: /email address/i }), { target: { value: 'tenant@flatpack.com' } })
    fireEvent.change(screen.getByLabelText(/^password$/i), { target: { value: 'FlatpackLocal!2026Strong' } })
    fireEvent.click(screen.getByRole('button', { name: /^sign in$/i }))

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/overview'))
    expect(antiforgeryRequests).toBe(2)
    expect(api.setAntiforgeryToken).toHaveBeenNthCalledWith(1, 'token-1')
    expect(api.setAntiforgeryToken).toHaveBeenNthCalledWith(2, 'token-2')
  })
})
