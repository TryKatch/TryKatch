import type { Meta, StoryObj } from '@storybook/react-vite'
import { Badge } from './primitives'
import { DataTable, type DataTableColumn } from './patterns'

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

const meta = { title: 'Flatpack/Patterns/Data table' } satisfies Meta
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
