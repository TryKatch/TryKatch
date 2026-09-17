import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { customFetch } from '@trykatch/api-client'
import { readFileSync } from 'node:fs'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { UserManagementPage } from './Pages'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(), customFetch: vi.fn(),
}))

const lifecycle = { status: 'Active' }
const roles = ['Member', 'Admin', 'Owner'].map((name, index) => ({
  id: `role-${index}`, name, description: `${name} access`, isSystem: true,
  canAssign: name !== 'Owner', permissions: [], lifecycle,
}))
let permissions: string[]
const styles = readFileSync('src/styles.css', 'utf8')

beforeEach(() => {
  permissions = ['members.read', 'members.manage', 'roles.read', 'roles.manage']
  vi.mocked(customFetch).mockImplementation(async (url, options) => {
    if (url === '/api/v1/access') return { membershipId: 'current', permissions }
    if (url.startsWith('/api/v1/roles')) return roles
    if (url === '/api/v1/invitations' && options?.method === 'POST') return {
      invitation: { id: 'invitation', email: 'new@example.test', roleId: 'role-1', lifecycle },
      invitationUrl: 'https://example.test/invite/test-only-token', emailDelivered: false,
    }
    return []
  })
})
afterEach(() => { cleanup(); vi.clearAllMocks() })

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><UserManagementPage /></QueryClientProvider>)
}

it('keeps invitation radios aligned beside readable role descriptions', () => {
  expect(styles).toMatch(/\.dialog-form\s+\.organization-role-picker\s+label:not\(\.checkbox\)\s*\{[^}]*display:\s*flex/s)
  expect(styles).toMatch(/\.dialog-form\s+\.organization-role-picker\s+input\[type="radio"\]\s*\{[^}]*width:\s*16px/s)
})

it('layers shared modal content above the mobile navigation drawer', () => {
  const content = Number(styles.match(/\.dialog-content\s*\{[^}]*z-index:\s*(\d+)/s)?.[1])
  const overlay = Number(styles.match(/\.dialog-overlay\s*\{[^}]*z-index:\s*(\d+)/s)?.[1])
  const sidebar = Math.max(...[...styles.matchAll(/\.sidebar\s*\{[^}]*z-index:\s*(\d+)/gs)].map((match) => Number(match[1])))
  expect(overlay).toBeGreaterThan(sidebar)
  expect(content).toBeGreaterThan(overlay)
})

it('sends the selected role when inviting and excludes roles the caller cannot assign', async () => {
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Invite person' }))
  fireEvent.change(screen.getByRole('textbox', { name: 'Email address' }), { target: { value: 'new@example.test' } })
  fireEvent.click(await screen.findByRole('radio', { name: 'Admin' }))
  expect(screen.queryByRole('radio', { name: /Owner/ })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Create invitation' }))
  await waitFor(() => expect(customFetch).toHaveBeenCalledWith('/api/v1/invitations', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ email: 'new@example.test', roleId: 'role-1', expiresInDays: 7 }),
  })))
  expect(await screen.findByText('Workspace role: Admin')).toBeInTheDocument()
})

it('does not fetch administration data or offer controls to an ordinary member entering the URL', async () => {
  permissions = ['members.read', 'roles.read', 'projects.read', 'projects.manage']
  mount()
  expect(await screen.findByText('Only workspace administrators can manage people and roles.')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Invite person' })).not.toBeInTheDocument()
  expect(vi.mocked(customFetch).mock.calls.map(([url]) => url)).toEqual(['/api/v1/access'])
})
