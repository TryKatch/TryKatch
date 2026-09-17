import { defineTableContribution, defineTableExtensionPoint, ModuleProvider, useTableContributions, WebModuleCatalog, type WebModule } from '@trykatch/module-sdk'
import { DataTable, RowActions } from '@trykatch/ui'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

interface Invoice { id: string; name: string }
const point = defineTableExtensionPoint<Invoice>('invoices.table', 'Invoices table')
const selected = vi.fn()
const base: WebModule = {
  id: 'invoices', name: 'Invoices', version: '1.0.0', description: 'Invoices',
  requires: [], optionalDependencies: [], routes: [], navigation: [],
  extensionPoints: [point], extensions: [],
}
const reporting: WebModule = {
  ...base, id: 'reporting', requires: ['invoices'], extensionPoints: [],
  tableContributions: [defineTableContribution(point, {
    id: 'reporting.extra', order: 10, requiredPermission: 'reporting.read',
    columns: [{ id: 'reporting.id', header: 'Record ID', cell: row => row.id }],
    actions: row => [{ id: 'reporting.select', label: 'Select invoice', icon: 'view', onSelect: () => selected(row.id) }],
  })],
}
const catalog = new WebModuleCatalog([reporting, base])

function TableHost({ permissions = [] }: { permissions?: string[] }) {
  const table = useTableContributions(point, permissions, {
    columns: [{ id: 'name', header: 'Name', cell: row => row.name }],
  })
  return <DataTable ariaLabel="Invoices" data={[{ id: '42', name: 'Invoice 42' }]} getRowId={row => row.id}
    searchable={false} columns={[...table.columns, {
      id: 'actions', header: 'Actions', cell: row => <RowActions label={'Actions for ' + row.name} actions={table.actions(row)} />,
    }]} />
}

afterEach(() => { cleanup(); selected.mockClear() })

describe('table contributions in the host UI', () => {
  it('renders permission-granted columns and invokes actions with the typed row', () => {
    render(<ModuleProvider catalog={catalog}><TableHost permissions={['reporting.read']} /></ModuleProvider>)
    expect(screen.getByRole('columnheader', { name: 'Record ID' })).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: '42' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Invoice 42' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Select invoice' }))
    expect(selected).toHaveBeenCalledExactlyOnceWith('42')
  })

  it('does not render restricted columns or actions without permission', () => {
    render(<ModuleProvider catalog={catalog}><TableHost /></ModuleProvider>)
    expect(screen.queryByRole('columnheader', { name: 'Record ID' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Invoice 42' }))
    expect(screen.queryByRole('menuitem', { name: 'Select invoice' })).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Name' })).toBeInTheDocument()
  })
})
