import type { Meta, StoryObj } from '@storybook/react-vite'
import { Badge, Surface } from '@trykatch/ui'
import { ProductLogo } from './ProductLogo'

export default {
  title: 'Welcome/Catalogue guide',
  parameters: { docs: { description: { component: 'A development-only catalogue of real Trykatch UI. All API examples use deterministic fixtures; no backend, Docker or Aspire is required.' } } },
} satisfies Meta
type Story = StoryObj
export const StartHere: Story = {
  render: () => <Surface style={{ padding: 'clamp(20px, 4vw, 48px)', maxWidth: 960 }}>
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, color: 'var(--accent-ink)' }}><ProductLogo size={24} /><strong>Trykatch</strong></div><p style={{ marginTop: 24 }}><Badge tone="info">UI catalogue</Badge> <Badge>Storybook 10.6</Badge></p>
    <h1 style={{ fontSize: 'clamp(24px, 4vw, 36px)', margin: '16px 0' }}>Build confidently. Keep the brand.</h1>
    <p style={{ color: 'var(--muted)', maxWidth: 640, lineHeight: 1.7 }}>Explore actual application components, reusable control recipes and real module pages. Use the toolbar to check the theme, language and viewport. Open a story to inspect its interactions and accessibility results.</p>
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 220px), 1fr))', gap: 16, marginTop: 28 }}>
      {[
        ['Forms & validation', 'Required fields, correction, saving and server errors.', '/docs/forms-validation--docs'],
        ['Tables & columns', 'Search, sorting, pagination and protected columns.', '/docs/datatables-collections--docs'],
        ['Roles & permissions', 'Least-privilege selection and grant boundaries.', '/docs/application-ui-role-management--docs'],
        ['Document uploads', 'File selection, classification and upload validation.', '/docs/module-ui-documents--docs'],
      ].map(([title, description, path]) => <Surface key={title} style={{ padding: 20 }}><h2 style={{ fontSize: 16, marginBottom: 8 }}><a href={`/?path=${path}`} target="_top">{title}</a></h2><p style={{ color: 'var(--muted)', lineHeight: 1.6 }}>{description}</p></Surface>)}
    </div>
    <p style={{ marginTop: 28, fontSize: 12, color: 'var(--muted)' }}>Fixtures are not proof of backend authorization or organization isolation. Those remain covered by backend and end-to-end tests.</p>
  </Surface>,
}
