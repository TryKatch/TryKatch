import type { Meta, StoryObj } from '@storybook/react-vite'
import { ModuleFeatureProvider, type FeatureAvailabilityState } from '@trykatch/module-sdk'
import { delay, http, HttpResponse } from 'msw'
import { useEffect, useState, type ComponentType } from 'react'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { workspaceModules } from '../../modules'
import { ArchivePage } from './ArchivePage'

const organizationId = 'storybook-organization'
const records = [
  { id: 'project-atlas', name: 'Atlas', description: 'A recoverable delivery workspace', lifecycle: { status: 'Archived', archivedAt: '2026-09-07T10:00:00Z' } },
  { id: 'project-borealis', name: 'Borealis', description: 'Pending deletion with a reason', lifecycle: { status: 'Deleted', deletedAt: '2026-09-08T10:00:00Z', deletionReason: 'The planning workspace was replaced.' } },
]
let deletionAttempts = 0

const featureState = (reads: boolean, writes: boolean): FeatureAvailabilityState => ({
  organizationId,
  snapshot: { organizationId, values: { 'projects.reads': reads, 'projects.writes': writes } },
  status: 'success',
})

function withFeatures(state: FeatureAvailabilityState) {
  return (Story: ComponentType) => <ModuleFeatureProvider catalog={workspaceModules} state={state}><Story /></ModuleFeatureProvider>
}

function DisableWritesWhileDeleting() {
  const [writes, setWrites] = useState(true)
  useEffect(() => {
    const disableWrites = () => setWrites(false)
    window.addEventListener('storybook:disable-project-writes', disableWrites)
    return () => window.removeEventListener('storybook:disable-project-writes', disableWrites)
  }, [])
  return <ModuleFeatureProvider catalog={workspaceModules} state={featureState(true, writes)}>
    <ArchivePage />
  </ModuleFeatureProvider>
}

function FeatureCheckWhileDeleting() {
  const [status, setStatus] = useState<'loading' | 'success'>('success')
  useEffect(() => {
    const startCheck = () => setStatus('loading')
    const finishCheck = () => setStatus('success')
    window.addEventListener('storybook:start-archive-feature-check', startCheck)
    window.addEventListener('storybook:finish-archive-feature-check', finishCheck)
    return () => {
      window.removeEventListener('storybook:start-archive-feature-check', startCheck)
      window.removeEventListener('storybook:finish-archive-feature-check', finishCheck)
    }
  }, [])
  const state: FeatureAvailabilityState = status === 'loading'
    ? { organizationId, status }
    : featureState(true, true)
  return <ModuleFeatureProvider catalog={workspaceModules} state={state}><ArchivePage /></ModuleFeatureProvider>
}

const permissions = ['projects.read', 'projects.manage']
const api = [
  http.get('*/api/v1/projects', () => HttpResponse.json({ items: records, page: 1, pageSize: 100, totalCount: records.length })),
  http.post('*/api/v1/projects/:projectId/restore', () => new HttpResponse(null, { status: 204 })),
  http.delete('*/api/v1/projects/:projectId', () => { deletionAttempts++; return new HttpResponse(null, { status: 204 }) }),
]

const meta = {
  title: 'Application UI/Archive',
  component: ArchivePage,
  decorators: [withFeatures(featureState(true, true))],
  parameters: { msw: { handlers: {
    auth: [
      http.get('*/api/v1/access', () => HttpResponse.json({ organizationId, membershipId: 'storybook-membership', permissions })),
      http.get('*/api/v1/workspace/current', () => HttpResponse.json({ organizationId, membershipId: 'storybook-membership', permissions })),
      http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' })),
    ],
    api,
  } } },
} satisfies Meta<typeof ArchivePage>
export default meta
type Story = StoryObj<typeof meta>

export const Populated: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Atlas')).toBeVisible()
    await expect(canvas.getByText('Borealis')).toBeVisible()
    await expect(canvas.getByLabelText('Archive summary')).toHaveTextContent('2')
  },
}

export const ReadsDisabled: Story = {
  decorators: [withFeatures(featureState(false, true))],
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByRole('heading', { name: 'Archive' })).toBeVisible()
    await expect(canvas.queryByText('Atlas')).not.toBeInTheDocument()
    await expect(canvas.getByLabelText('Archive summary').querySelectorAll('strong')[0]).toHaveTextContent('0')
  },
}

export const WritesDisabled: Story = {
  decorators: [withFeatures(featureState(true, false))],
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Atlas')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for Atlas' }))
    await expect(canvas.getByRole('menuitem', { name: 'View' })).toBeVisible()
    await expect(canvas.queryByRole('menuitem', { name: 'Restore' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('menuitem', { name: 'Delete' })).not.toBeInTheDocument()
  },
}

export const ReadOnlyPermission: Story = {
  parameters: { msw: { handlers: { auth: [
    http.get('*/api/v1/access', () => HttpResponse.json({ organizationId, membershipId: 'storybook-reader', permissions: ['projects.read'] })),
    http.get('*/api/v1/workspace/current', () => HttpResponse.json({ organizationId, membershipId: 'storybook-reader', permissions: ['projects.read'] })),
  ] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Atlas')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for Atlas' }))
    await expect(canvas.getByRole('menuitem', { name: 'View' })).toBeVisible()
    await expect(canvas.queryByRole('menuitem', { name: 'Restore' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('menuitem', { name: 'Delete' })).not.toBeInTheDocument()
  },
}

export const WritesDisabledWhileDeletionIsOpen: Story = {
  render: () => <DisableWritesWhileDeleting />,
  play: async ({ canvasElement }) => {
    deletionAttempts = 0
    const canvas = within(canvasElement)
    await userEvent.click(await canvas.findByRole('button', { name: 'Actions for Atlas' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Delete' }))
    const dialog = within(canvasElement.ownerDocument.body).getByRole('dialog')
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Reason for deletion' }), 'No longer required')
    // Model a refreshed policy from an operator in another browser while this modal is open.
    canvasElement.ownerDocument.defaultView!.dispatchEvent(new Event('storybook:disable-project-writes'))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).not.toBeInTheDocument())
    await expect(deletionAttempts).toBe(0)
  },
}

export const DeleteDraftSurvivesFeatureCheck: Story = {
  render: () => <FeatureCheckWhileDeleting />,
  play: async ({ canvasElement }) => {
    deletionAttempts = 0
    const canvas = within(canvasElement)
    await userEvent.click(await canvas.findByRole('button', { name: 'Actions for Atlas' }))
    await userEvent.click(canvas.getByRole('menuitem', { name: 'Delete' }))
    const dialog = within(canvasElement.ownerDocument.body).getByRole('dialog')
    const reason = 'No longer required by this team'
    const reasonField = within(dialog).getByRole('textbox', { name: 'Reason for deletion' })
    await userEvent.type(reasonField, reason)
    const staleSubmit = within(dialog).getByRole('button', { name: 'Request deletion' })

    canvasElement.ownerDocument.defaultView!.dispatchEvent(new Event('storybook:start-archive-feature-check'))
    await waitFor(() => {
      expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).not.toBeInTheDocument()
      expect(canvas.queryByText('Atlas')).not.toBeInTheDocument()
    })
    staleSubmit.click()
    await expect(deletionAttempts).toBe(0)

    canvasElement.ownerDocument.defaultView!.dispatchEvent(new Event('storybook:finish-archive-feature-check'))
    await expect(await within(canvasElement.ownerDocument.body).findByRole('dialog')).toBeVisible()
    await expect(within(canvasElement.ownerDocument.body).getByRole('textbox', { name: 'Reason for deletion' })).toHaveValue(reason)
    await expect(await canvas.findByText('Atlas')).toBeVisible()
    await expect(deletionAttempts).toBe(0)
  },
}

export const FrenchDark: Story = { globals: { locale: 'fr', theme: 'dark' } }
export const Loading: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/projects', async () => { await delay('infinite'); return HttpResponse.json({}) })] } } },
}
