import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { FloatingInput, FloatingTextarea } from './FloatingField'
import { Surface } from './primitives'
import { PasswordField } from './PasswordField'
import { useState } from 'react'
import { SearchField } from './SearchField'

function FilterableSearchDemo() {
  const [search, setSearch] = useState('')
  const [types, setTypes] = useState(['Invoice', 'Contract', 'Report'])
  const documents = [{ name: 'Delivery invoice', type: 'Invoice' }, { name: 'Supplier contract', type: 'Contract' }, { name: 'Quality report', type: 'Report' }]
  const matches = documents.filter((document) => types.includes(document.type) && document.name.toLowerCase().includes(search.toLowerCase()))
  return <><SearchField label="Search documents" value={search} onChange={(event) => setSearch(event.target.value)} filters={<div className="search-filter-options">
    <label className="search-filter-select-all"><input type="checkbox" checked={types.length === 3} onChange={() => setTypes(types.length === 3 ? [] : ['Invoice', 'Contract', 'Report'])} />Select all</label>
    {['Invoice', 'Contract', 'Report'].map((type) => <label key={type}><input type="checkbox" checked={types.includes(type)} onChange={() => setTypes((current) => current.includes(type) ? current.filter((value) => value !== type) : [...current, type])} /><span>{type}</span><small>1</small></label>)}
  </div>} /><div aria-live="polite" style={{ marginTop: 12 }}>{matches.length ? matches.map((document) => <p key={document.name}>{document.name}</p>) : <p>No results</p>}</div></>
}

const meta = {
  title: 'Forms/Floating fields', component: FloatingInput,
  args: { label: 'Email address', type: 'email', autoComplete: 'email', required: true },
  decorators: [(Story) => <Surface style={{ padding: 24, maxWidth: 520 }}><Story /></Surface>],
  parameters: { docs: { description: { component: 'Shared branded floating-label controls. Labels move on focus and remain above filled/autofilled values. Labels are real associated HTML labels—not placeholders. Provide translated label, help and error messages; errors are linked with aria-describedby and aria-invalid. Date/time labels always float. Use FloatingSelect for branded dropdowns and native file pickers separately.' } } },
} satisfies Meta<typeof FloatingInput>
export default meta
type Story = StoryObj<typeof meta>
export const Empty: Story = { play: async ({ canvasElement }) => {
  const input = within(canvasElement).getByRole('textbox', { name: 'Email address' })
  await expect(input.getBoundingClientRect().height).toBe(38)
} }
export const Filled: Story = { args: { defaultValue: 'alex@example.test' }, play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  const input = canvas.getByRole('textbox', { name: 'Email address' })
  await expect(canvas.getByText('Email address', { selector: 'label' }).getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top)
} }
export const FocusAndType: Story = { play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  const input = canvas.getByRole('textbox', { name: 'Email address' })
  const label = canvas.getByText('Email address', { selector: 'label' })
  await expect(label.getBoundingClientRect().top).toBeGreaterThan(input.getBoundingClientRect().top + 8)
  await userEvent.click(input)
  await expect(getComputedStyle(label).backgroundColor).toBe('rgba(0, 0, 0, 0)')
  await expect(getComputedStyle(input).boxShadow).toBe('none')
  await expect(getComputedStyle(input).outlineStyle).toBe('none')
  await waitFor(() => expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top))
  await waitFor(() => expect(label.getBoundingClientRect().bottom).toBeGreaterThan(input.getBoundingClientRect().top))
  await waitFor(() => expect(Math.abs((label.getBoundingClientRect().top + label.getBoundingClientRect().bottom) / 2 - input.getBoundingClientRect().top)).toBeLessThan(1))
  await expect(input.parentElement!.querySelector('legend')!.getBoundingClientRect().width).toBeGreaterThan(label.getBoundingClientRect().width - 1)
  await userEvent.type(input, 'alex@example.test')
  await userEvent.tab()
  await expect(input).toHaveValue('alex@example.test')
  await expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top)
  await userEvent.clear(input)
  await userEvent.tab()
  await waitFor(() => expect(label.getBoundingClientRect().top).toBeGreaterThan(input.getBoundingClientRect().top + 8))
  await expect(input.parentElement!.querySelector('legend')!.getBoundingClientRect().width).toBe(0)
} }
export const ValidationError: Story = { args: { defaultValue: 'not-an-email', error: 'Enter a valid email address.', description: 'We use this address for account notifications.' } }
export const Search: Story = {
  args: { label: 'Search documents', type: 'search', required: false, leadingIcon: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></svg> },
  render: () => <FilterableSearchDemo />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const input = canvas.getByRole('searchbox', { name: 'Search documents' })
    const label = canvas.getByText('Search documents', { selector: 'label' })
    await expect(input.getBoundingClientRect().height).toBe(34)
    await userEvent.click(label)
    await expect(input).toHaveFocus()
    await waitFor(() => expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top))
    await userEvent.type(input, 'invoice')
    await userEvent.tab()
    await expect(input).toHaveValue('invoice')
    await expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top)
    await userEvent.clear(input)
    await userEvent.tab()
    await waitFor(() => expect(label.getBoundingClientRect().top).toBeGreaterThan(input.getBoundingClientRect().top + 4))
  },
}
export const SearchFilters: Story = {
  render: () => <FilterableSearchDemo />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const body = within(canvasElement.ownerDocument.body)
    const trigger = canvas.getByRole('button', { name: 'Filters' })
    await userEvent.click(trigger)
    const filters = within(body.getByRole('dialog', { name: 'Filters' }))
    await userEvent.click(filters.getByRole('checkbox', { name: 'Select all' }))
    await expect(canvas.getByText('No results')).toBeVisible()
    await userEvent.click(filters.getByRole('checkbox', { name: /Invoice/ }))
    await expect(canvas.getByText('Delivery invoice')).toBeVisible()
    await expect(canvas.queryByText('Supplier contract')).not.toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await expect(body.queryByRole('dialog', { name: 'Filters' })).not.toBeInTheDocument()
    await expect(trigger).toHaveFocus()
    await userEvent.click(trigger)
    await userEvent.click(canvas.getByRole('searchbox', { name: 'Search documents' }))
    await expect(body.queryByRole('dialog', { name: 'Filters' })).not.toBeInTheDocument()
  },
}
export const SearchFiltersDark: Story = { ...SearchFilters, globals: { theme: 'dark' } }
export const SearchFiltersFrench: Story = {
  globals: { locale: 'fr' },
  render: () => <SearchField label="Rechercher des documents" filterLabel="Filtres" closeFiltersLabel="Fermer les filtres" filters={<div className="search-filter-options"><label><input type="checkbox" defaultChecked /><span>Factures</span><small>1</small></label><label><input type="checkbox" defaultChecked /><span>Contrats</span><small>1</small></label></div>} />,
  play: async ({ canvasElement }) => {
    await userEvent.click(within(canvasElement).getByRole('button', { name: 'Filtres' }))
    await expect(within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Filtres' })).toBeVisible()
    await userEvent.keyboard('{Escape}')
  },
}
export const Disabled: Story = { args: { disabled: true, defaultValue: 'alex@example.test' } }
export const ReadOnly: Story = { args: { readOnly: true, defaultValue: 'alex@example.test' } }
export const Numeric: Story = { args: { label: 'Quantity', type: 'number', min: 1, defaultValue: 1 } }
export const Date: Story = { args: { label: 'Delivery date', type: 'date' } }
export const Textarea: Story = { render: () => <FloatingTextarea label="Description" defaultValue="Quarterly delivery report" description="Optional notes for your team." maxLength={2000} /> }
export const Password: Story = { render: () => <PasswordField label="Password" autoComplete="new-password" required />, play: async ({ canvasElement }) => {
  const canvas = within(canvasElement)
  const input = canvas.getByLabelText(/Password/, { selector: 'input' })
  await expect(input.getBoundingClientRect().height).toBe(38)
  await userEvent.type(input, 'Storybook-example-only')
  await userEvent.click(canvas.getByRole('button', { name: 'Show password' }))
  await expect(input).toHaveAttribute('type', 'text')
  await expect(input).toHaveValue('Storybook-example-only')
  await userEvent.click(canvas.getByRole('button', { name: 'Hide password' }))
  await expect(input).toHaveAttribute('type', 'password')
} }
export const Dark: Story = { args: { defaultValue: 'alex@example.test' }, globals: { theme: 'dark' }, play: async ({ canvasElement }) => {
  const label = within(canvasElement).getByText('Email address', { selector: 'label' })
  await expect(getComputedStyle(label).backgroundColor).toBe('rgba(0, 0, 0, 0)')
  await expect(label.getBoundingClientRect().bottom).toBeGreaterThan(within(canvasElement).getByRole('textbox', { name: 'Email address' }).getBoundingClientRect().top)
} }
export const French: Story = { args: { label: 'Adresse e-mail', defaultValue: 'alex@example.test', description: 'Adresse utilisée pour les notifications.' }, globals: { locale: 'fr' } }
export const HostFormContexts: Story = {
  render: () => <div style={{ display: 'grid', gap: 20 }}>
    {['auth-card', 'dialog-form', 'profile-form', 'dialog-form role-identity-fields'].map((className) => <section key={className} className={className} style={{ width: '100%' }}>
      <FloatingInput label="Email address" type="email" required />
      <FloatingTextarea label="Description" required rows={2} />
      <PasswordField label="Password" required />
    </section>)}
  </div>,
  play: async ({ canvasElement }) => {
    for (const input of Array.from(canvasElement.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('.floating-control-input'))) {
      const label = canvasElement.querySelector<HTMLLabelElement>(`label[for="${input.id}"]`)!
      const marker = label.querySelector('span')!
      await expect(label.getBoundingClientRect().height).toBeLessThan(20)
      await expect(marker.getBoundingClientRect().bottom).toBeLessThanOrEqual(label.getBoundingClientRect().bottom + 1)
      await userEvent.click(label)
      await expect(input).toHaveFocus()
      await waitFor(() => expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top))
      await expect(getComputedStyle(label).backgroundColor).toBe('rgba(0, 0, 0, 0)')
    }
  },
}
export const HostFormContextsDark: Story = { ...HostFormContexts, globals: { theme: 'dark' } }
