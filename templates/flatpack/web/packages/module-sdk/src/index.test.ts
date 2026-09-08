import { describe, expect, it } from 'vitest'
import { FlatpackWebModuleCatalog, defineFlatpackWebModule, type FlatpackWebModule } from './index'

const Empty = () => null

function module(id: string, requires: readonly string[] = []): FlatpackWebModule {
  return defineFlatpackWebModule({
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

describe('FlatpackWebModuleCatalog', () => {
  it('orders dependencies before their consumers', () => {
    const catalog = new FlatpackWebModuleCatalog([module('reporting', ['projects']), module('projects')])
    expect(catalog.modules.map((item) => item.id)).toEqual(['projects', 'reporting'])
  })

  it('rejects missing dependencies', () => {
    expect(() => new FlatpackWebModuleCatalog([module('reporting', ['projects'])])).toThrow("requires missing module 'projects'")
  })

  it('rejects route collisions', () => {
    const first = module('projects')
    const second = { ...module('reporting'), routes: [{ id: 'reporting.home', path: '/projects' as const, component: Empty }] }
    expect(() => new FlatpackWebModuleCatalog([first, second])).toThrow("Duplicate Flatpack route path '/projects'")
  })

  it('rejects dependency cycles', () => {
    expect(() => new FlatpackWebModuleCatalog([module('projects', ['reporting']), module('reporting', ['projects'])])).toThrow('dependency cycle')
  })

  it('orders an installed optional dependency but allows it to be absent', () => {
    const reporting = { ...module('reporting'), optionalDependencies: ['projects'] }
    expect(new FlatpackWebModuleCatalog([reporting]).modules.map((item) => item.id)).toEqual(['reporting'])
    expect(new FlatpackWebModuleCatalog([reporting, module('projects')]).modules.map((item) => item.id)).toEqual(['projects', 'reporting'])
  })

  it('rejects an extension targeting an undeclared host', () => {
    const invalid = {
      ...module('reporting'),
      extensions: [{ id: 'reporting.summary', point: 'dashboard.summary.after', order: 10, component: Empty }],
    }
    expect(() => new FlatpackWebModuleCatalog([invalid])).toThrow("targets unknown point 'dashboard.summary.after'")
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

    const catalog = new FlatpackWebModuleCatalog([contributor, host])

    expect(catalog.extensionsFor('host.page.actions').map((extension) => extension.id))
      .toEqual(['contributor.primary-action', 'contributor.secondary-action'])
  })

  it('applies explicit overrides and rejects unknown targets', () => {
    const projects = module('projects')
    const hidden = new FlatpackWebModuleCatalog([projects], { routes: { 'projects.home': null } })
    expect(hidden.routes).toEqual([])
    expect(() => new FlatpackWebModuleCatalog([projects], { routes: { 'unknown.route': null } })).toThrow("unknown Flatpack route 'unknown.route'")
  })
})
