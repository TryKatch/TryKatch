import { fireEvent, render, screen, within } from '@testing-library/react'
import { DataTable, DeleteConfirmationDialog, RowActions, type DataTableColumn } from '@trykatch/ui'
import { describe, expect, it, vi } from 'vitest'

interface Row { id: string; name: string; owner: string }

const data: Row[] = [
  { id: '2', name: 'Zulu', owner: 'David' },
  { id: '1', name: 'Atlas', owner: 'Amina' },
]

const columns: DataTableColumn<Row>[] = [
  { id: 'name', header: 'Name', hideable: false, cell: (row) => row.name, sortValue: (row) => row.name, searchValue: (row) => `${row.name} ${row.owner}` },
  { id: 'owner', header: 'Owner', cell: (row) => row.owner, sortValue: (row) => row.owner },
]

describe('DataTable', () => {
  it('owns reusable filtering, sorting, density and column visibility behavior', () => {
    const { container } = render(<DataTable ariaLabel="Projects" data={data} columns={columns} getRowId={(row) => row.id} />)

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search table' }), { target: { value: 'Amina' } })
    expect(screen.getByText('Atlas')).toBeInTheDocument()
    expect(screen.queryByText('Zulu')).not.toBeInTheDocument()

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search table' }), { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: /Name/ }))
    const bodyRows = within(screen.getByRole('table', { name: 'Projects' })).getAllByRole('row').slice(1)
    expect(within(bodyRows[0]).getByText('Atlas')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Table settings' }))
    expect(screen.getByRole('dialog', { name: 'Table settings' })).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /Name \(required\)/ })).toBeDisabled()
    fireEvent.click(screen.getByLabelText('Compact'))
    expect(container.querySelector('.data-table')).toHaveClass('data-table-compact')

    fireEvent.click(screen.getByLabelText('Owner'))
    expect(screen.queryByRole('columnheader', { name: /Owner/ })).not.toBeInTheDocument()
  })

  it('reconciles visible columns when a reused table receives a new schema', () => {
    const peopleColumns: DataTableColumn<Row>[] = [
      { id: 'person', header: 'Person', hideable: false, cell: (row) => row.name },
      { id: 'access', header: 'Access', cell: (row) => row.owner },
      { id: 'actions', header: 'Actions', hideable: false, cell: () => null },
    ]
    const roleColumns: DataTableColumn<Row>[] = [
      { id: 'role', header: 'Role', hideable: false, cell: (row) => row.name },
      { id: 'type', header: 'Type', cell: () => 'System' },
      { id: 'permissions', header: 'Permissions', cell: () => '4 grants' },
      { id: 'actions', header: 'Actions', hideable: false, cell: () => null },
    ]
    const { rerender } = render(<DataTable ariaLabel="People" data={data} columns={peopleColumns} getRowId={(row) => row.id} />)

    rerender(<DataTable ariaLabel="Roles" data={data} columns={roleColumns} getRowId={(row) => row.id} />)

    expect(within(screen.getByRole('table', { name: 'Roles' })).getAllByRole('columnheader')).toHaveLength(4)
    expect(screen.getByRole('columnheader', { name: 'Role' })).toBeInTheDocument()
    expect(within(screen.getByRole('table', { name: 'Roles' })).getByText('Zulu')).toBeInTheDocument()
  })

  it('keeps large collections compact with one-at-a-time row disclosure and paging', () => {
    render(<DataTable
      ariaLabel="Access roles"
      data={data}
      columns={columns}
      getRowId={(row) => row.id}
      getRowExpansionLabel={(row) => row.name}
      renderExpandedRow={(row) => <div>{row.owner} owns {row.name}</div>}
      pageSize={1}
    />)

    fireEvent.click(screen.getByRole('button', { name: 'Show details for Zulu' }))
    expect(screen.getByText('David owns Zulu')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Hide details for Zulu' })).toHaveAttribute('aria-expanded', 'true')

    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    expect(screen.queryByText('David owns Zulu')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Show details for Atlas' })).toBeInTheDocument()
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument()
  })

  it('provides an ordered, accessible row action menu', () => {
    const view = vi.fn()
    const { container } = render(<RowActions label="Actions for Atlas" actions={[
      { label: 'View', icon: 'view', onSelect: view },
      { label: 'Edit', icon: 'edit', onSelect: vi.fn() },
      { label: 'Delete', icon: 'delete', danger: true, onSelect: vi.fn() },
      { label: 'Archive', icon: 'archive', onSelect: vi.fn() },
    ]} />)

    expect(container.querySelector('svg[data-icon="kebab"]')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Atlas' }))
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['View', 'Edit', 'Delete', 'Archive'])
    fireEvent.click(screen.getByRole('menuitem', { name: 'View' }))
    expect(view).toHaveBeenCalledOnce()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('requires an accountable reason before confirming a recoverable delete', () => {
    const confirm = vi.fn()
    render(<DeleteConfirmationDialog open recordName="Atlas" recordType="project" onOpenChange={vi.fn()} onConfirm={confirm} />)
    const submit = screen.getByRole('button', { name: 'Request deletion' })
    expect(submit).toBeDisabled()
    fireEvent.change(screen.getByRole('textbox', { name: /Reason for deletion/ }), { target: { value: 'wrong' } })
    expect(submit).toBeDisabled()
    fireEvent.change(screen.getByRole('textbox', { name: /Reason for deletion/ }), { target: { value: 'Created in the wrong organization' } })
    fireEvent.click(submit)
    expect(confirm).toHaveBeenCalledWith('Created in the wrong organization')
  })
})
