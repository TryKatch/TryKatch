import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, fn, userEvent, within } from 'storybook/test'
import { FloatingSelect, type FloatingSelectProps } from './FloatingSelect'
import { Surface } from './primitives'

function Controlled(props: FloatingSelectProps) {
  const [value, setValue] = useState(props.value)
  return <FloatingSelect {...props} value={value} onValueChange={setValue} />
}
const meta = {
  title: 'Forms/Floating dropdown', component: FloatingSelect,
  args: { label: 'Provider', value: 'deepseek', onValueChange: fn(), options: [{ value: 'deepseek', label: 'DeepSeek' }, { value: 'openai', label: 'OpenAI' }, { value: 'ollama', label: 'Ollama' }] },
  render: args => <Controlled {...args} />,
  decorators: [Story => <Surface style={{ padding: 24, maxWidth: 520 }}><Story /></Surface>],
  parameters: { docs: { description: { component: 'Branded 38 px dropdown with associated floating label, a bounded menu, typeahead and keyboard navigation. Use for custom dropdowns instead of drawing a second label or focus border. Translate labels, options, help and errors at the call site.' } } },
} satisfies Meta<typeof FloatingSelect>
export default meta
type Story = StoryObj<typeof meta>
export const Default: Story = {}
export const KeyboardSelection: Story = { play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  const trigger = canvas.getByRole('combobox', { name: 'Provider' })
  await expect(trigger.getBoundingClientRect().height).toBe(38)
  trigger.focus()
  await userEvent.keyboard('{Enter}{ArrowDown}{Enter}')
  await expect(trigger).toHaveTextContent('OpenAI')
  await expect(trigger).toHaveFocus()
  await expect(getComputedStyle(trigger).boxShadow).toBe('none')
  await expect(getComputedStyle(trigger).outlineStyle).toBe('none')
  await expect(getComputedStyle(canvas.getByText('Provider', { selector: 'label' })).backgroundColor).toBe('rgba(0, 0, 0, 0)')
  await userEvent.keyboard('{Enter}{Escape}')
  await expect(within(canvasElement.ownerDocument.body).queryByRole('listbox')).not.toBeInTheDocument()
  await expect(trigger).toHaveFocus()
} }
export const Disabled: Story = { args: { disabled: true }, play: async ({ canvasElement }) => { await expect(within(canvasElement).getByRole('combobox')).toBeDisabled() } }
export const ValidationError: Story = { args: { error: 'Choose an approved provider.', description: 'Your organization uses its own subscription.' } }
export const Dark: Story = { globals: { theme: 'dark' } }
export const French: Story = { globals: { locale: 'fr' }, args: { label: 'Fournisseur', description: 'Utilisez votre abonnement.' } }
