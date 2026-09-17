import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent, within } from 'storybook/test'
import { Button, Surface } from './primitives'
import { PasswordField } from './PasswordField'

export default { title: 'Forms/Native controls', parameters: { docs: { description: { component: 'Examples of the native controls already used by application forms. These are recipes, not new shared input components. Business validation remains server-owned.' } } } } satisfies Meta
type Story = StoryObj
function NativeForm({ disabled = false }: { disabled?: boolean }) {
  const [submitted, setSubmitted] = useState(false)
  return <Surface style={{ padding: 24 }}><form className="form-grid" onSubmit={(event) => { event.preventDefault(); setSubmitted(true) }}><fieldset disabled={disabled} style={{ display: 'grid', gap: 16, border: 0 }}>
    <label htmlFor="example-name">Name *</label><input id="example-name" required placeholder="Give this record a clear name" aria-describedby="name-help" /><small id="name-help">Required. Use a recognizable name.</small>
    <label htmlFor="example-search">Search</label><input id="example-search" type="search" placeholder="Search records…" />
    <PasswordField inputId="example-password" label="Password" />
    <label htmlFor="example-number">Quantity</label><input id="example-number" type="number" min="1" defaultValue="1" />
    <label htmlFor="example-date">Date</label><input id="example-date" type="date" defaultValue="2026-09-01" />
    <label htmlFor="example-description">Description</label><textarea id="example-description" placeholder="Optional notes" />
    <label htmlFor="example-type">Document type</label><select id="example-type"><option>Invoice</option><option>Contract</option><option>Report</option><option>Other</option></select>
    <label htmlFor="example-file">File</label><input id="example-file" type="file" accept=".pdf,.png,.jpg" />
    <label><input type="checkbox" /> Keep me signed in</label>
    <Button variant="primary" type="submit">Save</Button>
  </fieldset>{submitted && <p role="status">Example submitted. No backend request was made.</p>}</form></Surface>
}
export const AllControls: Story = { render: () => <NativeForm /> }
export const Disabled: Story = { render: () => <NativeForm disabled /> }
export const ValidationError: Story = { render: () => <Surface style={{ padding: 24 }}><label htmlFor="invalid-name">Name *</label><input id="invalid-name" aria-invalid="true" aria-describedby="invalid-help" /><p id="invalid-help" className="form-error" role="alert">Enter a name before saving.</p></Surface> }
export const ReadOnly: Story = { render: () => <><label htmlFor="readonly">Stable identifier</label><input id="readonly" readOnly value="invoice-001" /><p>This value cannot be renamed after creation.</p></> }
export const Submission: Story = {
  render: () => <NativeForm />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.type(canvas.getByRole('textbox', { name: 'Name *' }), 'Quarterly report')
    await userEvent.click(canvas.getByRole('button', { name: 'Save' }))
    await expect(canvas.getByRole('status')).toHaveTextContent('No backend request was made')
  },
}
