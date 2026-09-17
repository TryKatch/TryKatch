import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent, within } from 'storybook/test'
import { Button, Surface } from './primitives'
import { PasswordField } from './PasswordField'
import { FloatingInput, FloatingTextarea } from './FloatingField'

export default { title: 'Forms/Native controls', parameters: { docs: { description: { component: 'Examples of the native controls already used by application forms. These are recipes, not new shared input components. Business validation remains server-owned.' } } } } satisfies Meta
type Story = StoryObj
function NativeForm({ disabled = false }: { disabled?: boolean }) {
  const [submitted, setSubmitted] = useState(false)
  return <Surface style={{ padding: 24, maxWidth: 640 }}><form className="dialog-form" onSubmit={(event) => { event.preventDefault(); setSubmitted(true) }}><fieldset disabled={disabled} style={{ display: 'grid', gap: 12, border: 0, padding: 0 }}>
    <FloatingInput id="example-name" label="Name" required description="Required. Use a recognizable name." />
    <FloatingInput id="example-search" label="Search" type="search" />
    <PasswordField inputId="example-password" label="Password" />
    <FloatingInput id="example-number" label="Quantity" type="number" min="1" defaultValue="1" />
    <FloatingInput id="example-date" label="Date" type="date" defaultValue="2026-09-01" />
    <FloatingTextarea id="example-description" label="Description" description="Optional notes" />
    <label htmlFor="example-type">Document type</label><select id="example-type"><option>Invoice</option><option>Contract</option><option>Report</option><option>Other</option></select>
    <label htmlFor="example-file">File</label><input id="example-file" type="file" accept=".pdf,.png,.jpg" />
    <label><input type="checkbox" /> Keep me signed in</label>
    <Button variant="primary" type="submit">Save</Button>
  </fieldset>{submitted && <p role="status">Example submitted. No backend request was made.</p>}</form></Surface>
}
export const AllControls: Story = { render: () => <NativeForm /> }
export const Disabled: Story = { render: () => <NativeForm disabled /> }
export const ValidationError: Story = { render: () => <Surface className="dialog-form" style={{ padding: 24, maxWidth: 640 }}><label htmlFor="invalid-name">Name *<input id="invalid-name" aria-invalid="true" aria-describedby="invalid-help" /></label><p id="invalid-help" className="form-error" role="alert">Enter a name before saving.</p></Surface> }
export const ReadOnly: Story = { render: () => <Surface className="dialog-form" style={{ padding: 24, maxWidth: 640 }}><label htmlFor="readonly">Stable identifier<input id="readonly" readOnly value="invoice-001" /></label><p>This value cannot be renamed after creation.</p></Surface> }
export const Submission: Story = {
  render: () => <NativeForm />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.type(canvas.getByRole('textbox', { name: 'Name' }), 'Quarterly report')
    await userEvent.click(canvas.getByRole('button', { name: 'Save' }))
    await expect(canvas.getByRole('status')).toHaveTextContent('No backend request was made')
  },
}
