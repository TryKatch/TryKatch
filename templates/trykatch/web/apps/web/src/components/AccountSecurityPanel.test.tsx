import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { setAntiforgeryToken } from '@trykatch/api-client'
import axe from 'axe-core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../i18n/I18nProvider'
import { AccountSecurityPanel } from './AccountSecurityPanel'
import { AccountSecurityCompletionBoundary } from './AccountSecurityCompletion'

describe('AccountSecurityPanel', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
    localStorage.clear()
    sessionStorage.clear()
  })

  it('requires password proof before setup and keeps credentials out of browser storage', async () => {
    const calls: { path: string; body: Record<string, unknown> }[] = []
    setAntiforgeryToken('csrf-test')
    vi.stubGlobal('fetch', async (url: string, options: RequestInit) => {
      expect(new Headers(options.headers).get('X-CSRF-TOKEN')).toBe('csrf-test')
      const body = JSON.parse(String(options.body))
      calls.push({ path: url, body })
      if (url.endsWith('/reauthenticate')) return Response.json({ grant: 'one-use-grant', expiresAt: new Date(Date.now() + 300000).toISOString() })
      if (url.endsWith('/mfa/setup')) return Response.json({ enrollmentId: 'pending-id', sharedKey: 'PENDING-KEY', authenticatorUri: 'otpauth://pending', expiresAt: new Date(Date.now() + 600000).toISOString() })
      if (url.endsWith('/mfa/enable')) return Response.json({ codes: ['RECOVERY-ONE'], signInRequired: true })
      throw new Error('Unexpected network request')
    })
    render(<AccountSecurityCompletionBoundary><AccountSecurityPanel twoFactorEnabled={false} /></AccountSecurityCompletionBoundary>)
    fireEvent.click(screen.getByRole('button', { name: 'Set up MFA' }))
    expect(calls).toHaveLength(0)
    fireEvent.change(screen.getByLabelText(/^Current password/, { selector: 'input' }), { target: { value: 'Local-password-proof' } })
    fireEvent.click(screen.getByRole('button', { name: 'Verify and continue' }))
    expect(await screen.findByText('PENDING-KEY')).toBeInTheDocument()
    expect(calls[0]).toEqual({ path: '/api/v1/account/security/reauthenticate', body: { purpose: 'mfa.enroll', password: 'Local-password-proof', code: null, isRecoveryCode: false } })
    expect(calls[1]?.body).toEqual({ grant: 'one-use-grant' })
    expect(screen.queryByLabelText(/^Current password/, { selector: 'input' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Six-digit code'), { target: { value: '123456' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirm authenticator' }))
    expect(await screen.findByText('RECOVERY-ONE')).toBeInTheDocument()
    expect(screen.queryByText('PENDING-KEY')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'I saved my recovery codes — sign in' })).toHaveAttribute('href', '/login')
    expect(JSON.stringify(localStorage)).not.toMatch(/Local-password-proof|one-use-grant|PENDING-KEY|RECOVERY-ONE/)
    expect(sessionStorage.length).toBe(0)
    vi.stubGlobal('navigator', { language: 'en', clipboard: undefined })
    fireEvent.click(screen.getByRole('button', { name: 'Copy recovery codes' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Copy failed. Save your recovery codes manually.')
  })

  it('offers an accessible French verification flow for an enrolled account', async () => {
    localStorage.setItem('trykatch-locale', 'fr')
    const { container } = render(<I18nProvider><AccountSecurityCompletionBoundary><AccountSecurityPanel twoFactorEnabled /></AccountSecurityCompletionBoundary></I18nProvider>)
    fireEvent.click(screen.getByRole('button', { name: 'Remplacer l’authentificateur' }))
    expect(screen.getByLabelText(/^Mot de passe actuel/, { selector: 'input' })).toBeInTheDocument()
    expect(screen.getByLabelText('Code de l’authentificateur')).toBeRequired()
    fireEvent.click(screen.getByRole('checkbox', { name: 'Utiliser un code de récupération' }))
    expect(screen.getByLabelText('Code de récupération')).toBeRequired()
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })

  it.each([
    ['Replace authenticator', 'mfa.replace'],
    ['Regenerate recovery codes', 'mfa.recovery-codes'],
    ['Disable MFA', 'mfa.disable'],
  ])('uses a separate purpose for %s and clears failed proof fields', async (action, purpose) => {
    setAntiforgeryToken('csrf-test')
    const requests: Record<string, unknown>[] = []
    vi.stubGlobal('fetch', async (_url: string, options: RequestInit) => {
      requests.push(JSON.parse(String(options.body)))
      return Response.json({ title: 'reauthentication_failed', detail: 'Untrusted server detail must not be displayed.' }, { status: 403 })
    })
    render(<AccountSecurityCompletionBoundary><AccountSecurityPanel twoFactorEnabled /></AccountSecurityCompletionBoundary>)
    fireEvent.click(screen.getByRole('button', { name: action }))
    fireEvent.change(screen.getByLabelText(/^Current password/, { selector: 'input' }), { target: { value: 'Local-password-proof' } })
    fireEvent.change(screen.getByLabelText('Authenticator code'), { target: { value: '123456' } })
    fireEvent.click(screen.getByRole('button', { name: 'Verify and continue' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Verification failed.'))
    expect(requests).toEqual([{ purpose, password: 'Local-password-proof', code: '123456', isRecoveryCode: false }])
    expect(screen.getByLabelText(/^Current password/, { selector: 'input' })).toHaveValue('')
    expect(screen.getByLabelText('Authenticator code')).toHaveValue('')
    expect(screen.queryByText('Untrusted server detail must not be displayed.')).not.toBeInTheDocument()
  })

  it('cancels the server enrollment and clears its displayed key', async () => {
    setAntiforgeryToken('csrf-test')
    const cancelled: string[] = []
    vi.stubGlobal('fetch', async (url: string, options: RequestInit) => {
      if (url.endsWith('/reauthenticate')) return Response.json({ grant: 'one-use-grant', expiresAt: new Date(Date.now() + 300000).toISOString() })
      if (url.endsWith('/mfa/setup')) return Response.json({ enrollmentId: 'pending-id', sharedKey: 'PENDING-KEY', authenticatorUri: 'otpauth://pending', expiresAt: new Date(Date.now() + 600000).toISOString() })
      if (url.endsWith('/mfa/cancel')) { cancelled.push(JSON.parse(String(options.body)).enrollmentId); return new Response(null, { status: 204 }) }
      throw new Error('Unexpected request')
    })
    render(<AccountSecurityCompletionBoundary><AccountSecurityPanel twoFactorEnabled={false} /></AccountSecurityCompletionBoundary>)
    fireEvent.click(screen.getByRole('button', { name: 'Set up MFA' }))
    fireEvent.change(screen.getByLabelText(/^Current password/, { selector: 'input' }), { target: { value: 'Local-password-proof' } })
    fireEvent.click(screen.getByRole('button', { name: 'Verify and continue' }))
    expect(await screen.findByText('PENDING-KEY')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(cancelled).toEqual(['pending-id']))
    expect(screen.queryByText('PENDING-KEY')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Set up MFA' })).toBeInTheDocument()
  })
})
