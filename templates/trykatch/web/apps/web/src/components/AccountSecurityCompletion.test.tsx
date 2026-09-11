import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { focusManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { setAntiforgeryToken } from '@trykatch/api-client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../i18n/I18nProvider'
import { router } from '../router'

describe('account security completion in authenticated shells', () => {
  afterEach(() => {
    cleanup()
    focusManager.setFocused(undefined)
    vi.unstubAllGlobals()
    localStorage.clear()
    sessionStorage.clear()
  })

  it.each(['/profile', '/dashboard/profile'])('retains one-time codes after blur/refocus and a session 401 at %s', async (path) => {
    let signedOut = false
    vi.stubGlobal('scrollTo', () => {})
    vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
    vi.stubGlobal('fetch', async (url: string) => {
      if (url.endsWith('/auth/session')) return signedOut
        ? Response.json({ title: 'Unauthorized' }, { status: 401 })
        : Response.json({ displayName: 'Test User', email: 'test@example.test', hasPlatformAccess: true, isPlatformAdministrator: true, platformPermissions: [] })
      if (url === '/api/v1/access') return Response.json({ permissions: [] })
      if (url === '/api/v1/account') return Response.json({ displayName: 'Test User', email: 'test@example.test', emailConfirmed: true, twoFactorEnabled: false })
      if (url.endsWith('/reauthenticate')) return Response.json({ grant: 'SECRET-GRANT', expiresAt: new Date(Date.now() + 300000).toISOString() })
      if (url.endsWith('/mfa/setup')) return Response.json({ enrollmentId: 'pending-id', sharedKey: 'SECRET-KEY', authenticatorUri: 'otpauth://pending', expiresAt: new Date(Date.now() + 600000).toISOString() })
      if (url.endsWith('/mfa/enable')) {
        signedOut = true
        return Response.json({ codes: ['SECRET-RECOVERY-CODE'], signInRequired: true })
      }
      throw new Error(`Unexpected request: ${url}`)
    })
    setAntiforgeryToken('csrf-test')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0, gcTime: Infinity } } })
    const history = createMemoryHistory({ initialEntries: [path] })
    const testRouter = createRouter({ routeTree: router.options.routeTree, history, defaultPendingMinMs: 0 })
    const view = render(<I18nProvider><QueryClientProvider client={client}><RouterProvider router={testRouter} /></QueryClientProvider></I18nProvider>)
    // The real profile route is lazy-loaded; allow cold imports under the full suite.
    await waitFor(() => expect(screen.getByRole('button', { name: 'Set up MFA' })).toBeEnabled(), { timeout: 5000 })
    fireEvent.click(screen.getByRole('button', { name: 'Set up MFA' }))
    const panel = within(screen.getByRole('region', { name: 'Two-factor authentication' }))
    fireEvent.change(panel.getByLabelText(/^Current password/, { selector: 'input' }), { target: { value: 'SECRET-PASSWORD' } })
    fireEvent.click(panel.getByRole('button', { name: 'Verify and continue' }))
    expect(await screen.findByText('SECRET-KEY')).toBeInTheDocument()
    fireEvent.change(panel.getByLabelText('Six-digit code'), { target: { value: '123456' } })
    fireEvent.click(panel.getByRole('button', { name: 'Confirm authenticator' }))
    expect(await screen.findByText('SECRET-RECOVERY-CODE')).toBeInTheDocument()

    await act(async () => {
      window.dispatchEvent(new Event('blur'))
      focusManager.setFocused(false)
      window.dispatchEvent(new Event('focus'))
      focusManager.setFocused(true)
      // Also exercise an already scheduled/inactive refresh after the shell unmounts.
      await client.refetchQueries({ queryKey: ['me', 'session'], type: 'all' })
    })
    expect(client.getQueryState(['me', 'session'])?.error).toMatchObject({ status: 401 })
    expect(screen.getByText('SECRET-RECOVERY-CODE')).toBeInTheDocument()
    expect(view.container.querySelector('.app-shell')).toBeNull()
    expect(screen.getByRole('heading', { name: 'Security settings updated' })).toHaveFocus()
    expect(screen.getByRole('link', { name: 'I saved my recovery codes — sign in' })).toHaveAttribute('href', '/login')
    expect(JSON.stringify({ localStorage, sessionStorage, location: history.location, queries: client.getQueryCache().getAll().map((query) => query.state.data) })).not.toContain('SECRET-')
    const leaving = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(leaving)
    expect(leaving.defaultPrevented).toBe(true)
    // Even a router navigation cannot let an authenticated shell reclaim this handoff.
    await act(async () => { await testRouter.navigate({ to: '/login' }) })
    expect(screen.getByText('SECRET-RECOVERY-CODE')).toBeInTheDocument()
    const acknowledge = screen.getByRole('link', { name: 'I saved my recovery codes — sign in' })
    acknowledge.addEventListener('click', (event) => event.preventDefault())
    fireEvent.click(acknowledge)
    expect(screen.queryByText('SECRET-RECOVERY-CODE')).not.toBeInTheDocument()
    expect(view.container.querySelector('.app-shell')).toBeNull()
    const acknowledgedLeaving = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(acknowledgedLeaving)
    expect(acknowledgedLeaving.defaultPrevented).toBe(false)
    view.unmount()
    client.clear()
  }, 10000)
})
