import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent, within } from 'storybook/test'
import { Badge, Button, Dialog, EmptyState, Skeleton, Surface } from './primitives'

const meta = { title: 'Buttons/Variants', component: Button } satisfies Meta<typeof Button>
export default meta
type Story = StoryObj<typeof meta>
export const Buttons: Story = { render: () => <Surface style={{ padding: 16, display: 'flex', gap: 8 }}><Button variant="primary">Create project</Button><Button>Cancel</Button><Badge tone="success">Healthy</Badge></Surface> }
export const Primary: Story = { args: { variant: 'primary', children: 'Save changes' } }
export const Secondary: Story = { args: { variant: 'secondary', children: 'Cancel' } }
export const Ghost: Story = { args: { variant: 'ghost', children: 'View details' } }
export const Danger: Story = { args: { variant: 'danger', children: 'Request deletion' } }
export const Disabled: Story = { args: { disabled: true, children: 'Unavailable' } }
export const Busy: Story = { args: { disabled: true, 'aria-busy': true, children: 'Saving…' } }
export const WithIcon: Story = { args: { variant: 'primary', children: <><span aria-hidden="true">＋</span> Create</> } }
export const AsLink: Story = { render: () => <Button asChild><a href="#details">View details</a></Button> }

export const AllBadgeTones: Story = {
  render: () => <Surface style={{ padding: 16, display: 'flex', gap: 8, flexWrap: 'wrap' }}>{(['neutral', 'success', 'warning', 'danger', 'info'] as const).map((tone) => <Badge key={tone} tone={tone}>{tone}: {({ neutral: 'Archived', success: 'Active', warning: 'Pending deletion', danger: 'Failed', info: 'Processing' })[tone]}</Badge>)}</Surface>,
}
export const Feedback: Story = {
  render: () => <Surface style={{ padding: 16 }}><div role="status" aria-label="Loading records"><Skeleton className="h-8" /><p>Loading records…</p></div><EmptyState title="No records" description="Create your first record to get started." action={<Button variant="primary">Create record</Button>} /><div role="alert" className="form-error">Could not save. Your changes have been preserved.</div></Surface>,
}
function DialogExample() {
  const [open, setOpen] = useState(false)
  return <><Button onClick={() => setOpen(true)}>Open dialog</Button><Dialog open={open} onOpenChange={setOpen} title="Review changes" description="Check the details before continuing."><p>Your original record remains recoverable.</p><Button onClick={() => setOpen(false)}>Cancel</Button></Dialog></>
}
export const DialogInteraction: Story = {
  render: () => <DialogExample />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('button', { name: 'Open dialog' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(body.getByRole('dialog', { name: 'Review changes' })).toBeVisible()
    await userEvent.keyboard('{Escape}')
    await expect(body.queryByRole('dialog')).not.toBeInTheDocument()
  },
}
