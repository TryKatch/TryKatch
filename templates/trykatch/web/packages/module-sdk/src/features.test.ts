import { describe, expect, it } from 'vitest'
import { WebModuleCatalog, type ModuleFeatureDescriptor, type WebModule } from './index'

const Empty = () => null

function module(id: string, features?: readonly ModuleFeatureDescriptor[]): WebModule {
  return {
    id,
    name: id,
    version: '1.0.0',
    description: `${id} module.`,
    requires: [],
    optionalDependencies: [],
    routes: [{ id: `${id}.home`, path: `/${id}`, component: Empty }],
    navigation: [],
    extensionPoints: [],
    extensions: [],
    ...(features === undefined ? {} : { features }),
  }
}

const feature = (key: string, overrides: Partial<ModuleFeatureDescriptor> = {}): ModuleFeatureDescriptor => ({
  key,
  name: 'Feature name',
  description: 'Feature description.',
  defaultEnabled: true,
  ...overrides,
})

describe('WebModuleCatalog feature metadata', () => {
  it('treats legacy modules without feature metadata as having no features', () => {
    expect(new WebModuleCatalog([module('projects')]).features).toEqual([])
  })

  it('flattens explicitly enabled and disabled declarations without changing their defaults', () => {
    const reads = feature('projects.reads')
    const writes = feature('projects.writes', { name: 'Writes', description: 'Create and change projects.', defaultEnabled: false })

    expect(new WebModuleCatalog([module('projects', [reads, writes])]).features).toEqual([reads, writes])
  })

  it('accepts exactly the declared count and text length boundaries', () => {
    const descriptors = Array.from({ length: 100 }, (_, index) => feature(`projects.f${index}`))
    descriptors[0] = feature(`projects.${'a'.repeat(111)}`, {
      name: 'n'.repeat(160),
      description: 'd'.repeat(1000),
    })

    expect(new WebModuleCatalog([module('projects', descriptors)]).features).toHaveLength(100)
  })

  it.each([
    ['more than 100 features', Array.from({ length: 101 }, (_, index) => feature(`projects.f${index}`))],
    ['a key over 120 characters', [feature(`projects.${'a'.repeat(112)}`)]],
    ['an invalid key', [feature('Projects.reads')]],
    ['a key outside its module namespace', [feature('documents.reads')]],
    ['a blank name', [feature('projects.reads', { name: '  ' })]],
    ['a name over 160 characters', [feature('projects.reads', { name: 'n'.repeat(161) })]],
    ['a blank description', [feature('projects.reads', { description: '\n ' })]],
    ['a description over 1000 characters', [feature('projects.reads', { description: 'd'.repeat(1001) })]],
    ['a non-boolean default', [feature('projects.reads', { defaultEnabled: undefined as unknown as boolean })]],
    ['a duplicate key', [feature('projects.reads'), feature('projects.reads')]],
    ['a null feature entry', [null]],
    ['an unknown descriptor property', [{ ...feature('projects.reads'), futurePolicy: true }]],
  ])('rejects %s', (_label, features) => {
    expect(() => new WebModuleCatalog([module('projects', features as ModuleFeatureDescriptor[])])).toThrow()
  })

  it('rejects a non-array feature collection supplied at runtime', () => {
    const invalid = { ...module('projects'), features: { key: 'projects.reads' } }
    expect(() => new WebModuleCatalog([invalid as unknown as WebModule])).toThrow()
  })

  it('accepts exact expected manifest features independent of declaration order', () => {
    const reads = feature('projects.reads')
    const writes = feature('projects.writes', { name: 'Writes', description: 'Change projects.', defaultEnabled: false })

    const catalog = new WebModuleCatalog([module('projects', [reads, writes])], {}, [
      { moduleId: 'projects', feature: writes },
      { moduleId: 'projects', feature: reads },
    ])

    expect(catalog.features).toEqual([reads, writes])
  })

  it('uses an explicit empty expected list to detect undeclared manifest parity', () => {
    expect(() => new WebModuleCatalog([module('projects', [feature('projects.reads')])], {}, [])).toThrow()
  })

  it.each([
    ['a missing expected feature', [module('projects')], [{ moduleId: 'projects', feature: feature('projects.reads') }]],
    ['an extra expected feature', [module('projects', [feature('projects.reads')])], []],
    ['an unknown expected module', [module('projects')], [{ moduleId: 'documents', feature: feature('documents.reads') }]],
    ['a foreign-owner expected key', [module('projects')], [{ moduleId: 'projects', feature: feature('documents.reads') }]],
    ['a duplicate expected feature', [module('projects', [feature('projects.reads')])], [
      { moduleId: 'projects', feature: feature('projects.reads') },
      { moduleId: 'projects', feature: feature('projects.reads') },
    ]],
    ['a changed name', [module('projects', [feature('projects.reads')])], [{ moduleId: 'projects', feature: feature('projects.reads', { name: 'Changed' }) }]],
    ['a changed description', [module('projects', [feature('projects.reads')])], [{ moduleId: 'projects', feature: feature('projects.reads', { description: 'Changed' }) }]],
    ['a changed explicit default', [module('projects', [feature('projects.reads')])], [{ moduleId: 'projects', feature: feature('projects.reads', { defaultEnabled: false }) }]],
  ])('rejects manifest parity mismatch: %s', (_label, modules, expected) => {
    expect(() => new WebModuleCatalog(modules as WebModule[], {}, expected as { moduleId: string; feature: ModuleFeatureDescriptor }[])).toThrow()
  })

  it('rejects malformed expected feature records', () => {
    const invalid = [{ moduleId: 'projects', feature: null }]
    expect(() => new WebModuleCatalog([module('projects')], {}, invalid as unknown as { moduleId: string; feature: ModuleFeatureDescriptor }[])).toThrow()
  })
})
