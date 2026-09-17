import { cleanup, render, screen } from '@testing-library/react'
import { readFileSync } from 'node:fs'
import type { ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DashboardPage } from './Pages'

const styles = readFileSync('src/styles.css', 'utf8')

const overview = vi.hoisted(() => ({
  moduleMetrics: [], activeMembers: 1, pendingInvitations: 0, activeRoles: 4,
  membersWithAccess: 1, eventsToday: 1,
  recentActivity: [{ action: 'projects.created', title: 'Project created',
    targetDisplayName: 'A very long project name', actorDisplayName: 'Tenant Administrator',
    occurredAt: '2026-09-17T10:00:00Z' }],
}))

vi.mock('@tanstack/react-query', async (original) => ({
  ...await original<typeof import('@tanstack/react-query')>(),
  useQuery: () => ({ data: overview, isLoading: false, isError: false }),
}))
vi.mock('@tanstack/react-router', async (original) => ({
  ...await original<typeof import('@tanstack/react-router')>(),
  Link: ({ to, children }: { to: string; children: ReactNode }) => <a href={to}>{children}</a>,
}))

afterEach(cleanup)

describe('Dashboard activity layout', () => {
  it('owns its spacing without depending on the removed content-grid wrapper', () => {
    render(<DashboardPage />)
    const card = screen.getByRole('heading', { name: 'Recent activity' }).closest('.surface')
    expect(card).toHaveClass('workspace-activity-panel')
    expect(screen.getByText('Project created: A very long project name')).toBeInTheDocument()
    expect(styles).toMatch(/\.workspace-activity-panel\s*\{[^}]*overflow:\s*hidden/s)
    expect(styles).toMatch(/\.workspace-activity-panel\s*>\s*\.panel-title\s*\{[^}]*padding:\s*16px/s)
    expect(styles).toMatch(/\.workspace-activity-panel\s+\.activity-row\s*\{[^}]*padding:\s*14px\s+16px/s)
  })

  it('allows activity text to wrap without shrinking the status dot', () => {
    expect(styles).toMatch(/\.workspace-activity-panel\s+\.activity-row\s*>\s*div\s*\{[^}]*min-width:\s*0/s)
    expect(styles).toMatch(/\.workspace-activity-panel\s+\.activity-row\s+strong\s*\{[^}]*overflow-wrap:\s*anywhere/s)
    expect(styles).toMatch(/\.activity-dot\s*\{[^}]*flex-shrink:\s*0/s)
  })
})
