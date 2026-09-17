import type { Meta, StoryObj } from '@storybook/react-vite'
import { delay, http, HttpResponse } from 'msw'
import { expect, userEvent, within } from 'storybook/test'
import { ProjectsPage } from './ProjectsPage'

const records = [{ id: '00000000-0000-0000-0000-000000000010', name: 'Atlas', description: 'Organization project example', createdAt: '2026-09-01T10:00:00Z', lifecycle: { status: 'Active' } }]
const meta = { title: 'Module UI/Projects', component: ProjectsPage, parameters: { msw: { handlers: {
  auth: [http.get('*/api/v1/access', () => HttpResponse.json({ permissions: ['projects.read', 'projects.manage'] })), http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' }))],
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
