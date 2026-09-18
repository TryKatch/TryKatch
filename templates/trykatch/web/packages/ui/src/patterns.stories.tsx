import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { Badge } from './primitives'
import { DataTable, DeleteConfirmationDialog, DirtyFormBar, FilterBar, PageHeader, RowActions, type DataTableColumn } from './patterns'

interface ProjectRow {
  id: string
  name: string
  owner: string
  status: string
  updatedAt: string
}

const rows: ProjectRow[] = [
  { id: '1', name: 'Atlas', owner: 'Amina', status: 'Active', updatedAt: '2026-09-07T10:30:00Z' },
  { id: '2', name: 'Horizon', owner: 'David', status: 'Paused', updatedAt: '2026-09-06T14:15:00Z' },
]

const columns: DataTableColumn<ProjectRow>[] = [
  { id: 'name', header: 'Project', hideable: false, cell: (row) => <strong>{row.name}</strong>, sortValue: (row) => row.name, searchValue: (row) => `${row.name} ${row.owner}` },
  { id: 'owner', header: 'Owner', cell: (row) => row.owner, sortValue: (row) => row.owner },
  { id: 'status', header: 'Status', cell: (row) => <Badge tone={row.status === 'Active' ? 'success' : 'neutral'}>{row.status}</Badge>, sortValue: (row) => row.status },
  { id: 'updated', header: 'Updated', cell: (row) => new Date(row.updatedAt).toLocaleDateString(), sortValue: (row) => new Date(row.updatedAt) },
]

const meta = { title: 'DataTables/Collections' } satisfies Meta
export default meta
type Story = StoryObj<typeof meta>

export const EnterpriseCollection: Story = {
  render: () => <DataTable ariaLabel="Example projects" data={rows} columns={columns} getRowId={(row) => row.id} searchPlaceholder="Search projects…" initialSort={{ id: 'updated', direction: 'desc' }} />,
}

export const ExpandableCollection: Story = {
  render: () => <DataTable
    ariaLabel="Example expandable projects"
    data={rows}
    columns={columns}
    getRowId={(row) => row.id}
    getRowExpansionLabel={(row) => row.name}
    renderExpandedRow={(row) => <div style={{ padding: 16 }}>Additional details for <strong>{row.name}</strong> stay outside the compact summary row.</div>}
    pageSize={20}
  />,
}

const pagedRows: ProjectRow[] = [
  ...rows,
  { id: '3', name: 'Cedar', owner: 'Amina', status: 'Active', updatedAt: '2026-09-05T14:15:00Z' },
  { id: '4', name: 'Beacon', owner: 'David', status: 'Paused', updatedAt: '2026-09-04T14:15:00Z' },
  { id: '5', name: 'Delta', owner: 'Amina', status: 'Active', updatedAt: '2026-09-03T14:15:00Z' },
]
export const ClientPagination: Story = {
  render: () => <DataTable ariaLabel="Paged projects" data={pagedRows} columns={columns} getRowId={(row) => row.id} pageSize={2} initialSort={{ id: 'name', direction: 'asc' }} />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByRole('button', { name: 'Previous' })).toBeDisabled()
    await expect(canvas.getByText('Showing 1–2 of 5')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await expect(canvas.getByText('Page 2 of 3')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await expect(canvas.getByText('Showing 5–5 of 5')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Next' })).toBeDisabled()
    const search = canvas.getByRole('searchbox', { name: 'Search…' })
    await userEvent.type(search, 'Amina')
    const label = canvas.getByText('Search…', { selector: 'label' })
    await waitFor(() => expect(label.getBoundingClientRect().top).toBeLessThan(search.getBoundingClientRect().top))
    await expect(canvas.getByText('Page 1 of 2')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: /^Project/ }))
    await expect(canvas.getAllByRole('row')[1]).toHaveTextContent('Delta')
  },
}
export const ColumnSettings: Story = {
  render: () => <DataTable ariaLabel="Configurable projects" data={rows} columns={[...columns, { id: 'id', header: 'Identifier', defaultVisible: false, cell: (row) => row.id, align: 'right' }]} getRowId={(row) => row.id} />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.queryByRole('columnheader', { name: 'Identifier' })).not.toBeInTheDocument()
    await userEvent.click(canvas.getByRole('button', { name: 'Table settings' }))
    await expect(canvas.getByRole('checkbox', { name: 'Project (required)' })).toBeDisabled()
    await userEvent.click(canvas.getByRole('checkbox', { name: 'Identifier' }))
    await expect(canvas.getByRole('columnheader', { name: 'Identifier' })).toBeVisible()
    await userEvent.click(canvas.getByRole('radio', { name: 'Compact' }))
    await expect(canvas.getByRole('radio', { name: 'Compact' })).toBeChecked()
    await userEvent.keyboard('{Escape}')
    await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()
  },
}
export const Empty: Story = { render: () => <DataTable ariaLabel="Empty projects" data={[]} columns={columns} getRowId={(row) => row.id} /> }
export const Compact: Story = { render: () => <DataTable ariaLabel="Compact projects" data={rows} columns={columns} getRowId={(row) => row.id} initialDensity="compact" /> }
export const Spacious: Story = { render: () => <DataTable ariaLabel="Spacious projects" data={rows} columns={columns} getRowId={(row) => row.id} initialDensity="spacious" /> }
export const RowActionMenu: Story = { render: () => <RowActions label="Actions for Atlas" actions={(['view', 'edit', 'archive', 'restore', 'revoke', 'delete'] as const).map((icon) => ({ icon, label: icon, onSelect: () => undefined, danger: icon === 'delete', disabled: icon === 'revoke' }))} /> }
export const PageAndFilters: Story = { render: () => <><PageHeader eyebrow="Application" title="Projects" description="Organization-owned work." /><FilterBar placeholder="Search projects…"><Badge tone="info">Active records</Badge></FilterBar></> }
export const SearchFilterDropdown: Story = {
  render: () => {
    const [activeOnly, setActiveOnly] = useState(false)
    return <DataTable ariaLabel="Filtered projects" data={activeOnly ? rows.filter((row) => row.status === 'Active') : rows} columns={columns} getRowId={(row) => row.id} searchFilters={<div className="search-filter-options"><label><input type="checkbox" checked={activeOnly} onChange={(event) => setActiveOnly(event.target.checked)} /><span>Active only</span><small>1</small></label></div>} />
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('button', { name: 'Filters' }))
    const filters = within(within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Filters' }))
    await userEvent.click(filters.getByRole('checkbox', { name: /Active only/ }))
    await expect(canvas.getByRole('table', { name: 'Filtered projects' })).toHaveTextContent('Atlas')
    await expect(canvas.queryByText('Horizon')).not.toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await userEvent.click(canvas.getByRole('button', { name: 'Table settings' }))
    await expect(within(canvasElement.ownerDocument.body).getByRole('dialog', { name: 'Table settings' })).toBeVisible()
  },
}
export const UnsavedChanges: Story = { render: () => <DirtyFormBar visible onSave={() => undefined} onDiscard={() => undefined} /> }
function DeleteExample() {
  const [open, setOpen] = useState(true)
  const [reason, setReason] = useState('')
  return <><DeleteConfirmationDialog open={open} recordName="Atlas" recordType="project" onOpenChange={setOpen} onConfirm={(value) => { setReason(value); setOpen(false) }} />{reason && <p role="status">Deletion requested: {reason}</p>}</>
}
export const AccountableDeletion: Story = {
  render: () => <DeleteExample />,
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = within(body.getByRole('dialog'))
    await userEvent.type(dialog.getByRole('textbox'), 'Record is no longer needed')
    await userEvent.click(dialog.getByRole('button', { name: 'Request deletion' }))
    await expect(body.getByRole('status')).toHaveTextContent('Record is no longer needed')
  },
}
