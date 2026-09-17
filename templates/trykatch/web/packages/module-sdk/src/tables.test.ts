import { describe, expect, it } from 'vitest'
import { defineTableContribution, defineTableExtensionPoint, WebModuleCatalog, type WebModule } from './index'

interface Invoice { id: string; total: number }
const invoices = defineTableExtensionPoint<Invoice>('invoices.table', 'Invoice list columns and actions')
const row: Invoice = { id: '42', total: 120 }
const module = (id: string, extra: Partial<WebModule> = {}): WebModule => ({
  id, name: id, version: '1.0.0', description: id, requires: [], optionalDependencies: [],
  routes: [], navigation: [], extensionPoints: [], extensions: [], ...extra,
})
const host = module('invoices', { extensionPoints: [invoices] })
const column = (id: string) => ({ id, header: id, cell: (invoice: Invoice) => invoice.total })

describe('typed table contributions', () => {
  it('combines base and extension columns with deterministic order and typed actions', () => {
    const selected: string[] = []
    const additions = module('reporting', {
      requires: ['invoices'],
      tableContributions: [
        defineTableContribution(invoices, { id: 'reporting.z', order: 10, columns: [column('last')] }),
        defineTableContribution(invoices, { id: 'reporting.a', order: 10, columns: [column('first')],
          actions: invoice => [{ id: 'reporting.export', label: 'Export', icon: 'view', onSelect: () => selected.push(invoice.id) }] }),
      ],
    })
    const table = new WebModuleCatalog([additions, host]).tableFor(invoices, [], { columns: [column('base')] })
    expect(table.columns.map(item => item.id)).toEqual(['base', 'first', 'last'])
    expect(table.columns[1].cell(row)).toBe(120)
    table.actions(row)[0].onSelect()
    expect(selected).toEqual(['42'])
  })

  it('fails closed for permission-restricted presentation contributions', () => {
    const extra = defineTableContribution(invoices, { id: 'reporting.cost', order: 0, requiredPermission: 'reporting.read', columns: [column('cost')] })
    const catalog = new WebModuleCatalog([host, module('reporting', { tableContributions: [extra] })])
    expect(catalog.tableFor(invoices, []).columns).toHaveLength(0)
    expect(catalog.tableFor(invoices, ['reporting.read']).columns).toHaveLength(1)
  })

  it('rejects copied or undeclared points and duplicate contribution IDs', () => {
    const copied = defineTableExtensionPoint<Invoice>('invoices.table', 'An unrelated row contract')
    const extra = defineTableContribution(copied, { id: 'reporting.cost', order: 0, columns: [column('cost')] })
    expect(() => new WebModuleCatalog([host, module('reporting', { tableContributions: [extra] })])).toThrow('exported data-table point object')
    const valid = defineTableContribution(invoices, { id: 'reporting.cost', order: 0 })
    expect(() => new WebModuleCatalog([host, module('reporting', { tableContributions: [valid, valid] })])).toThrow('Duplicate Trykatch table contribution')
    expect(() => new WebModuleCatalog([host]).tableFor(copied, [])).toThrow('Unknown Trykatch table point')
  })

  it('detects duplicate column and action IDs including base entries', () => {
    const extra = defineTableContribution(invoices, { id: 'reporting.cost', order: 0, columns: [column('cost')],
      actions: () => [{ id: 'export', label: 'Export', icon: 'view', onSelect() {} }] })
    const catalog = new WebModuleCatalog([host, module('reporting', { tableContributions: [extra] })])
    expect(() => catalog.tableFor(invoices, [], { columns: [column('cost')] })).toThrow('table column id')
    const table = catalog.tableFor(invoices, [], { actions: () => [{ id: 'export', label: 'Export', icon: 'view', onSelect() {} }] })
    expect(() => table.actions(row)).toThrow('table action id')
  })

  it('rejects rebinding an erased contribution to a different row contract', () => {
    const extra = defineTableContribution(invoices, { id: 'reporting.cost', order: 0, columns: [column('cost')] })
    const other = defineTableExtensionPoint<{ title: string }>('other.table', 'Other records')
    const rebound = { ...extra, point: other }
    expect(() => new WebModuleCatalog([host, module('reporting', { extensionPoints: [other], tableContributions: [rebound] })]))
      .toThrow('exported data-table point object')
  })

  it('supports stable-ID disable and replacement overrides', () => {
    const extra = defineTableContribution(invoices, { id: 'reporting.cost', order: 0, columns: [column('cost')] })
    const modules = [host, module('reporting', { tableContributions: [extra] })]
    expect(new WebModuleCatalog(modules, { tableContributions: { 'reporting.cost': null } }).tableFor(invoices, []).columns).toHaveLength(0)
    expect(() => new WebModuleCatalog(modules, { tableContributions: { missing: null } })).toThrow('unknown Trykatch table contribution')
  })
})

// These compile-time contracts run under the normal workspace type check.
function invalidContracts() {
  defineTableContribution(invoices, {
    id: 'reporting.invalid', order: 0,
    // @ts-expect-error A different DTO cannot be used at an invoice point.
    columns: [{ id: 'other', header: 'Other', cell: (other: { title: string }) => other.title }],
  })
  const other = defineTableExtensionPoint<{ title: string }>('other.table', 'Other')
  // @ts-expect-error Row contracts are invariant, not interchangeable strings.
  const wrong: typeof invoices = other
  return wrong
}
void invalidContracts
