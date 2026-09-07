import type { Meta, StoryObj } from '@storybook/react-vite'
import { Badge, Button, Surface } from './primitives'

const meta = { title: 'Flatpack/Primitives', component: Button } satisfies Meta<typeof Button>
export default meta
type Story = StoryObj<typeof meta>
export const Buttons: Story = { render: () => <Surface style={{ padding: 16, display: 'flex', gap: 8 }}><Button variant="primary">Create project</Button><Button>Cancel</Button><Badge tone="success">Healthy</Badge></Surface> }
