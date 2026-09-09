import { describe, expect, it } from 'vitest'
import { TrykatchWebModuleCatalog, defineTrykatchWebModule, type TrykatchWebModule } from './index'

const Empty = () => null

function module(id: string, requires: readonly string[] = []): TrykatchWebModule {
  return defineTrykatchWebModule({
    id,
    name: id,
    version: '1.0.0',
    description: `${id} module.`,
    requires,
    optionalDependencies: [],
    routes: [{ id: `${id}.home`, path: `/${id}`, component: Empty }],
    navigation: [],
    extensionPoints: [],
    extensions: [],
  })
}

describe('TrykatchWebModuleCatalog', () => {
  it('orders dependencies before their consumers', () => {
    const catalog = new TrykatchWebModuleCatalog([module('reporting', ['projects']), module('projects')])
    expect(catalog.modules.map((item) => item.id)).toEqual(['projects', 'reporting'])
  })

  it('rejects missing dependencies', () => {
    expect(() => new TrykatchWebModuleCatalog([module('reporting', ['projects'])])).toThrow("requires missing module 'projects'")
  })

  it('rejects route collisions', () => {
    const first = module('projects')
    const second = { ...module('reporting'), routes: [{ id: 'reporting.home', path: '/projects' as const, component: Empty }] }
    expect(() => new TrykatchWebModuleCatalog([first, second])).toThrow("Duplicate Trykatch route path '/projects'")
  })

  it('rejects dependency cycles', () => {
    expect(() => new TrykatchWebModuleCatalog([module('projects', ['reporting']), module('reporting', ['projects'])])).toThrow('dependency cycle')
  })

  it('orders an installed optional dependency but allows it to be absent', () => {
    const reporting = { ...module('reporting'), optionalDependencies: ['projects'] }
    expect(new TrykatchWebModuleCatalog([reporting]).modules.map((item) => item.id)).toEqual(['reporting'])
    expect(new TrykatchWebModuleCatalog([reporting, module('projects')]).modules.map((item) => item.id)).toEqual(['projects', 'reporting'])
  })

  it('rejects an extension targeting an undeclared host', () => {
    const invalid = {
      ...module('reporting'),
      extensions: [{ id: 'reporting.summary', point: 'dashboard.summary.after', order: 10, component: Empty }],
    }
    expect(() => new TrykatchWebModuleCatalog([invalid])).toThrow("targets unknown point 'dashboard.summary.after'")
  })

  it('orders named extension contributions deterministically', () => {
    const host = {
      ...module('host'),
      extensionPoints: [{ id: 'host.page.actions', description: 'Page actions', kind: 'ui-slot' as const }],
    }
    const contributor = {
      ...module('contributor', ['host']),
      extensions: [
        { id: 'contributor.secondary-action', point: 'host.page.actions', order: 20, component: Empty },
        { id: 'contributor.primary-action', point: 'host.page.actions', order: 10, requiredPermission: 'things.manage', component: Empty },
      ],
    }

    const catalog = new TrykatchWebModuleCatalog([contributor, host])

    expect(catalog.extensionsFor('host.page.actions').map((extension) => extension.id))
      .toEqual(['contributor.primary-action', 'contributor.secondary-action'])
  })

  it('applies explicit overrides and rejects unknown targets', () => {
    const projects = module('projects')
    const hidden = new TrykatchWebModuleCatalog([projects], { routes: { 'projects.home': null } })
    expect(hidden.routes).toEqual([])
    expect(() => new TrykatchWebModuleCatalog([projects], { routes: { 'unknown.route': null } })).toThrow("unknown Trykatch route 'unknown.route'")
  })
})
