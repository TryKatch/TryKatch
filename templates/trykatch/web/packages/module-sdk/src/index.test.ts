import { describe, expect, it } from 'vitest'
import { WebModuleCatalog, defineWebModule, type WebModule } from './index'

const Empty = () => null

function module(id: string, requires: readonly string[] = []): WebModule {
  return defineWebModule({
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

describe('WebModuleCatalog', () => {
  it('orders dependencies before their consumers', () => {
    const catalog = new WebModuleCatalog([module('reporting', ['projects']), module('projects')])
    expect(catalog.modules.map((item) => item.id)).toEqual(['projects', 'reporting'])
  })

  it('rejects missing dependencies', () => {
    expect(() => new WebModuleCatalog([module('reporting', ['projects'])])).toThrow("requires missing module 'projects'")
  })

  it('rejects route collisions', () => {
    const first = module('projects')
    const second = { ...module('reporting'), routes: [{ id: 'reporting.home', path: '/projects' as const, component: Empty }] }
    expect(() => new WebModuleCatalog([first, second])).toThrow("Duplicate Trykatch route path '/projects'")
  })

  it('rejects dependency cycles', () => {
    expect(() => new WebModuleCatalog([module('projects', ['reporting']), module('reporting', ['projects'])])).toThrow('dependency cycle')
  })

  it('orders an installed optional dependency but allows it to be absent', () => {
    const reporting = { ...module('reporting'), optionalDependencies: ['projects'] }
    expect(new WebModuleCatalog([reporting]).modules.map((item) => item.id)).toEqual(['reporting'])
    expect(new WebModuleCatalog([reporting, module('projects')]).modules.map((item) => item.id)).toEqual(['projects', 'reporting'])
  })

  it('rejects an extension targeting an undeclared host', () => {
    const invalid = {
      ...module('reporting'),
      extensions: [{ id: 'reporting.summary', point: 'dashboard.summary.after', order: 10, component: Empty }],
    }
    expect(() => new WebModuleCatalog([invalid])).toThrow("targets unknown point 'dashboard.summary.after'")
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

    const catalog = new WebModuleCatalog([contributor, host])

    expect(catalog.extensionsFor('host.page.actions').map((extension) => extension.id))
      .toEqual(['contributor.primary-action', 'contributor.secondary-action'])
  })

  it('applies explicit overrides and rejects unknown targets', () => {
    const projects = module('projects')
    const hidden = new WebModuleCatalog([projects], { routes: { 'projects.home': null } })
    expect(hidden.routes).toEqual([])
    expect(() => new WebModuleCatalog([projects], { routes: { 'unknown.route': null } })).toThrow("unknown Trykatch route 'unknown.route'")
  })

  it('collects module-owned realtime query invalidations and deduplicates finite subscriptions', () => {
    const subscription = { policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 }, kind: 0 }
    const contribution = (id: string) => ({
      id,
      subscription,
      matches: (notification: { moduleId: string }) => notification.moduleId === 'projects',
      queryKeys: [['/api/v1/projects', { page: 1 }], ['projects']],
    })
    const catalog = new WebModuleCatalog([
      { ...module('projects'), realtimeInvalidations: [contribution('projects.changed')] },
      { ...module('reporting', ['projects']), realtimeInvalidations: [contribution('reporting.projects')] },
    ])

    expect(catalog.realtimeInvalidations.map((item) => item.id)).toEqual(['projects.changed', 'reporting.projects'])
    expect(catalog.realtimeSubscriptions).toEqual([subscription])
    expect(catalog.realtimeInvalidations[0].matches({
      moduleId: 'projects', eventKey: 'projects.created', schemaVersion: 1, eventId: 'event-1',
      audience: { policy: subscription.policy, kind: 0 },
    })).toBe(true)
  })

  it('rejects an invalid module-owned realtime audience selector', () => {
    const invalid = {
      ...module('projects'),
      realtimeInvalidations: [{
        id: 'projects.changed',
        subscription: { policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 }, kind: 2 },
        matches: () => true,
        queryKeys: [['projects']],
      }],
    }
    expect(() => new WebModuleCatalog([invalid])).toThrow("invalid audience selector")
  })
})
