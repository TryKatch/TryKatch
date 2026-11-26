import { describe, expect, it } from 'vitest'
import { isModuleFeatureEnabled, type FeatureAvailabilityState, type OrganizationFeatureSnapshot, WebModuleCatalog, type WebModule } from './index'

const Empty = () => null

function catalog() {
  const module: WebModule = {
    id: 'projects', name: 'Projects', version: '1.0.0', description: 'Projects module.',
    requires: [], optionalDependencies: [],
    routes: [{ id: 'projects.home', path: '/projects', component: Empty }],
    navigation: [], extensionPoints: [], extensions: [],
    features: [
      { key: 'projects.reads', name: 'Reads', description: 'Read projects.', defaultEnabled: false },
      { key: 'projects.writes', name: 'Writes', description: 'Change projects.', defaultEnabled: true },
    ],
  }
  return new WebModuleCatalog([module])
}

const snapshot = (organizationId: string, values: Readonly<Record<string, boolean>>): OrganizationFeatureSnapshot => ({ organizationId, values })
const state = (organizationId: string | undefined, featureSnapshot: OrganizationFeatureSnapshot | undefined, status: FeatureAvailabilityState['status'] = 'success'): FeatureAvailabilityState => ({ organizationId, snapshot: featureSnapshot, status })

describe('organization feature availability selection', () => {
  const installed = catalog()

  it('requires a successful snapshot for the exact current organization', () => {
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-1', { 'projects.reads': true })), 'projects.reads')).toBe(true)
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-2', { 'projects.reads': true })), 'projects.reads')).toBe(false)
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-1', { 'projects.reads': true }), 'loading'), 'projects.reads')).toBe(false)
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-1', { 'projects.reads': true }), 'error'), 'projects.reads')).toBe(false)
  })

  it('fails closed for missing organization, snapshot, values, and undeclared keys', () => {
    expect(isModuleFeatureEnabled(installed, state(undefined, snapshot('org-1', { 'projects.reads': true })), 'projects.reads')).toBe(false)
    expect(isModuleFeatureEnabled(installed, state('org-1', undefined), 'projects.reads')).toBe(false)
    expect(isModuleFeatureEnabled(installed, state('org-1', { organizationId: 'org-1' } as OrganizationFeatureSnapshot), 'projects.reads')).toBe(false)
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-1', { 'projects.unknown': true })), 'projects.unknown')).toBe(false)
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-1', {})), 'projects.reads')).toBe(false)
  })

  it('does not fall back to declared defaults when no evaluated value is present', () => {
    expect(isModuleFeatureEnabled(installed, state('org-1', snapshot('org-1', {})), 'projects.writes')).toBe(false)
  })

  it('keeps organization feature values independent', () => {
    const availability = state('org-1', snapshot('org-1', { 'projects.reads': true, 'projects.writes': false }))
    expect(isModuleFeatureEnabled(installed, availability, 'projects.reads')).toBe(true)
    expect(isModuleFeatureEnabled(installed, availability, 'projects.writes')).toBe(false)
  })
})
