import { describe, expect, it } from 'vitest'
import { WebModuleCatalog, type ModuleFeatureDescriptor, type WebModule } from './index'

const Empty = () => null
const reads: ModuleFeatureDescriptor = { key: 'projects.reads', name: 'Reads', description: 'Read projects.', defaultEnabled: true }
const module: WebModule = {
  id: 'projects', name: 'Projects', version: '1.0.0', description: 'Projects module.',
  requires: [], optionalDependencies: [],
  routes: [{ id: 'projects.home', path: '/projects', component: Empty }],
  navigation: [{ id: 'projects.nav', section: 'work', order: 1, to: '/projects', label: 'Projects', icon: Empty }],
  extensionPoints: [], extensions: [], features: [reads],
}

describe('feature bindings in installed module metadata', () => {
  it('allows declared feature requirements alongside permissions on workspace routes and navigation', () => {
    expect(() => new WebModuleCatalog([{
      ...module,
      routes: [{ id: 'projects.home', path: '/projects', component: Empty, requiredPermission: 'projects.read', requiredFeature: 'projects.reads' }],
      navigation: [{ id: 'projects.nav', section: 'work', order: 1, to: '/projects', label: 'Projects', icon: Empty, requiredPermission: 'projects.read', requiredFeature: 'projects.reads' }],
    }])).not.toThrow()
  })

  it.each([
    ['route', { ...module, routes: [{ id: 'projects.home', path: '/projects', component: Empty, requiredFeature: 'projects.unknown' }] }],
    ['navigation', { ...module, navigation: [{ id: 'projects.nav', section: 'work', order: 1, to: '/projects', label: 'Projects', icon: Empty, requiredFeature: 'projects.unknown' }] }],
    ['archive read', { ...module, archiveResources: [{ kind: 'projects', typeLabel: 'Projects', readPermission: 'projects.read', managePermission: 'projects.manage', readFeature: 'projects.unknown', load: async () => [], restore: async () => undefined }] }],
    ['archive manage', { ...module, archiveResources: [{ kind: 'projects', typeLabel: 'Projects', readPermission: 'projects.read', managePermission: 'projects.manage', manageFeature: 'projects.unknown', load: async () => [], restore: async () => undefined }] }],
  ] as const)('rejects an unknown %s feature binding', (_label, invalid) => {
    expect(() => new WebModuleCatalog([invalid as unknown as WebModule])).toThrow()
  })

  it.each([
    ['platform route', { ...module, routes: [{ id: 'projects.home', path: '/projects', component: Empty, surface: 'platform' as const, requiredFeature: 'projects.reads' }] }],
    ['platform navigation', { ...module, navigation: [{ id: 'projects.nav', section: 'work', order: 1, to: '/projects', label: 'Projects', icon: Empty, surface: 'platform' as const, requiredFeature: 'projects.reads' }] }],
  ] as const)('rejects organization feature requirements on a %s', (_label, invalid) => {
    expect(() => new WebModuleCatalog([invalid as unknown as WebModule])).toThrow()
  })

  it('validates effective route overrides after applying them', () => {
    expect(() => new WebModuleCatalog([module], {
      routes: { 'projects.home': { id: 'projects.home', path: '/projects', component: Empty, requiredFeature: 'projects.unknown' } },
    })).toThrow()
  })

  it('validates effective navigation overrides after applying them', () => {
    expect(() => new WebModuleCatalog([module], {
      navigation: { 'projects.nav': { id: 'projects.nav', section: 'work', order: 1, to: '/projects', label: 'Projects', icon: Empty, requiredFeature: 'projects.unknown' } },
    })).toThrow()
  })

  it('accepts legacy modules with no feature-bound metadata', () => {
    const { features: _features, ...legacy } = module
    expect(() => new WebModuleCatalog([legacy])).not.toThrow()
  })
})
