import type { Meta, StoryObj } from '@storybook/react-vite'
import { ModuleFeatureProvider, ModuleProvider, WebModuleCatalog, type FeatureAvailabilityState } from '@trykatch/module-sdk'
import { delay, http, HttpResponse } from 'msw'
import { useEffect, useState, type ComponentType } from 'react'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { ProjectsPage } from './ProjectsPage'
import { projectsModule } from './module'

const records = [{ id: '00000000-0000-0000-0000-000000000010', name: 'Atlas', description: 'Organization project example', createdAt: '2026-09-01T10:00:00Z', lifecycle: { status: 'Active' } }]
const organizationId = 'storybook-organization'
const catalog = new WebModuleCatalog([projectsModule])
const featureState = (reads: boolean, writes: boolean): FeatureAvailabilityState => ({
  organizationId,
  snapshot: { organizationId, values: { 'projects.reads': reads, 'projects.writes': writes } },
  status: 'success',
})
const enabledFeatures = featureState(true, true)
let writeAttempts = 0

function withFeatures(state: FeatureAvailabilityState) {
  return (Story: ComponentType) => <ModuleProvider catalog={catalog}>
    <ModuleFeatureProvider catalog={catalog} state={state}><Story /></ModuleFeatureProvider>
  </ModuleProvider>
}

function DisableWritesStory() {
  const [writes, setWrites] = useState(true)
  useEffect(() => {
    const disableWrites = () => setWrites(false)
    window.addEventListener('storybook:disable-project-writes', disableWrites)
    return () => window.removeEventListener('storybook:disable-project-writes', disableWrites)
  }, [])
  return <ModuleFeatureProvider catalog={catalog} state={featureState(true, writes)}>
    <ProjectsPage />
  </ModuleFeatureProvider>
}

const meta = { title: 'Module UI/Projects', component: ProjectsPage, decorators: [withFeatures(enabledFeatures)], parameters: { msw: { handlers: {
  auth: [http.get('*/api/v1/access', () => HttpResponse.json({ organizationId, membershipId: 'storybook-membership', permissions: ['projects.read', 'projects.manage'] })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))],
  api: [http.get('*/api/v1/projects', () => HttpResponse.json({ items: records, page: 1, pageSize: 100, totalCount: 1 }))],
} } } } satisfies Meta<typeof ProjectsPage>
export default meta
type Story = StoryObj<typeof meta>
export const Populated: Story = {}
export const Empty: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/projects', () => HttpResponse.json({ items: [], page: 1, pageSize: 100, totalCount: 0 }))] } } } }
export const Loading: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/projects', async () => { await delay('infinite'); return HttpResponse.json({}) })] } } } }
export const Error: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/projects', () => HttpResponse.json({ title: 'Could not load projects' }, { status: 503 }))] } } } }
export const CreateForm: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await canvas.findByText('Atlas')
    await userEvent.click(canvas.getByRole('button', { name: 'New project' }))
    await expect(within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Create project' })).toBeVisible()
  },
}
export const ReadsDisabled: Story = {
  decorators: [withFeatures(featureState(false, true))],
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.queryByText('Atlas')).not.toBeInTheDocument()
    await expect(canvas.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
  },
}
export const WritesDisabled: Story = {
  decorators: [withFeatures(featureState(true, false))],
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Atlas')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for Atlas' }))
    await expect(canvas.getByRole('menuitem', { name: 'View' })).toBeVisible()
    await expect(canvas.queryByRole('menuitem', { name: 'Edit' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('menuitem', { name: 'Archive' })).not.toBeInTheDocument()
  },
}
export const ReadOnlyPermission: Story = {
  parameters: { msw: { handlers: { auth: [
    http.get('*/api/v1/access', () => HttpResponse.json({ organizationId, membershipId: 'storybook-membership', permissions: ['projects.read'] })),
    http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' })),
  ] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(await canvas.findByText('Atlas')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'New project' })).not.toBeInTheDocument()
    await userEvent.click(canvas.getByRole('button', { name: 'Actions for Atlas' }))
    await expect(canvas.getByRole('menuitem', { name: 'View' })).toBeVisible()
    await expect(canvas.queryByRole('menuitem', { name: 'Edit' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('menuitem', { name: 'Archive' })).not.toBeInTheDocument()
  },
}
export const WritesDisabledWhileFormOpen: Story = {
  render: () => <DisableWritesStory />,
  parameters: { msw: { handlers: { api: [
    http.get('*/api/v1/projects', () => HttpResponse.json({ items: records, page: 1, pageSize: 100, totalCount: 1 })),
    http.post('*/api/v1/projects', () => { writeAttempts++; return HttpResponse.json(records[0]) }),
  ] } } },
  play: async ({ canvasElement }) => {
    writeAttempts = 0
    const canvas = within(canvasElement)
    await canvas.findByText('Atlas')
    await userEvent.click(canvas.getByRole('button', { name: 'New project' }))
    const dialog = within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Create project' })
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'Draft project')
    // Model a refreshed policy from an operator in another browser while this modal is open.
    canvasElement.ownerDocument.defaultView!.dispatchEvent(new Event('storybook:disable-project-writes'))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog', { name: 'Create project' })).not.toBeInTheDocument())
    await expect(writeAttempts).toBe(0)
  },
}
export const SavingFailed: Story = {
  parameters: { msw: { handlers: { api: [http.get('*/api/v1/projects', () => HttpResponse.json({ items: records, page: 1, pageSize: 100, totalCount: 1 })), http.post('*/api/v1/projects', () => HttpResponse.json({ title: 'Save failed. Your input is preserved.' }, { status: 503 }))] } } },
  play: async (context) => {
    await CreateForm.play!(context)
    const dialog = within(within(context.canvasElement.ownerDocument.body).getByRole('dialog'))
    await userEvent.type(dialog.getByRole('textbox', { name: 'Name' }), 'Delivery programme')
    await userEvent.click(dialog.getByRole('button', { name: 'Save project' }))
    await expect(await dialog.findByText('Save failed. Your input is preserved.')).toBeVisible()
  },
}
export const RequiredNameValidation: Story = {
  play: async (context) => {
    await CreateForm.play!(context)
    const dialog = within(within(context.canvasElement.ownerDocument.body).getByRole('dialog'))
    await userEvent.type(dialog.getByRole('textbox', { name: 'Name' }), '   ')
    await userEvent.click(dialog.getByRole('button', { name: 'Save project' }))
    await expect(dialog.getByRole('alert')).toHaveTextContent('Project name is required.')
    await expect(dialog.getByRole('textbox', { name: 'Name' })).toHaveAttribute('aria-invalid', 'true')
  },
}
export const Dark: Story = { globals: { theme: 'dark' } }
export const French: Story = { globals: { locale: 'fr' } }
export const EmptyDark: Story = { ...Empty, globals: { theme: 'dark' } }
export const EmptyFrench: Story = { ...Empty, globals: { locale: 'fr' } }
