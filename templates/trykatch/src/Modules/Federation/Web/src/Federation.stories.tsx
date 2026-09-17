import type { Meta, StoryObj } from '@storybook/react-vite'
import { delay, http, HttpResponse } from 'msw'
import { expect, userEvent, within } from 'storybook/test'
import { federationModule } from './index'

const Page = federationModule.routes[0].component
const connection = { id: 'demo-oidc', name: 'Example identity provider', issuer: 'https://identity.example.test', clientId: 'example-client', enabled: false, lastTestedAt: null, lastTestResult: null }
const meta = { title: 'Module UI/Federation', component: Page, parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/federation/connections/', () => HttpResponse.json([connection]))] } } } } satisfies Meta<typeof Page>
export default meta
type Story = StoryObj<typeof meta>
export const NotTested: Story = {}
export const Enabled: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/federation/connections/', () => HttpResponse.json([{ ...connection, enabled: true, lastTestedAt: '2026-09-01T10:00:00Z', lastTestResult: 'Discovery verified' }]))] } } } }
export const Empty: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/federation/connections/', () => HttpResponse.json([]))] } } } }
export const Loading: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/federation/connections/', async () => { await delay('infinite'); return HttpResponse.json([]) })] } } } }
export const Error: Story = { parameters: { msw: { handlers: { api: [http.get('*/api/v1/platform/federation/connections/', () => HttpResponse.json({ title: 'Connection catalogue unavailable' }, { status: 503 }))] } } } }
export const AddConnection: Story = { play: async ({ canvasElement }) => { const canvas = within(canvasElement); await userEvent.click(canvas.getByRole('button', { name: 'Add connection' })); await expect(within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Add an SSO connection' })).toBeVisible() } }
