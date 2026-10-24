import type { ComponentType, LazyExoticComponent } from 'react'
import { hasValidTableBinding, resolveTableContributions, type TableExtensionPoint, type TableParts, type WebTableContribution } from './tables'
import type { RealtimeAudience, WebRealtimeInvalidationContribution } from './realtime'
export { createRealtimeReconciler, type RealtimeAudience, type RealtimeHistoryPage, type RealtimeNotification, type RealtimePolicyRef, type RealtimeReconcilerOptions, type RealtimeResourceRef, type RealtimeState, type WebRealtimeInvalidationContribution } from './realtime'
export { defineTableExtensionPoint, defineTableContribution, type TableColumn, type TableAction, type TableParts, type TableExtensionPoint, type WebTableContribution } from './tables'

export { ModuleExtensionSlot, ModuleProvider, useTableContributions } from './extensions'
export { ModuleI18nProvider, useModuleI18n, type ModuleI18n, type ModuleMessageValues } from './i18n'
export { isModuleFeatureEnabled, ModuleFeatureProvider, useModuleFeature, useModuleFeatures, type FeatureAvailabilityState, type OrganizationFeatureSnapshot } from './features'

export interface ModuleIconProps {
  size?: string | number
}

export interface WebRoute {
  id: string
  path: `/${string}`
  surface?: 'workspace' | 'platform'
  requiredPermission?: string
  requiredFeature?: string
  component: ComponentType | LazyExoticComponent<ComponentType>
}

export interface NavigationContribution {
  id: string
  section: string
  order: number
  to: `/${string}`
  label: string
  labels?: Readonly<{ en: string; fr: string }>
  icon: ComponentType<ModuleIconProps>
  requiredPermission?: string
  requiredFeature?: string
  exact?: boolean
  surface?: 'workspace' | 'platform'
}

export type ExtensionPointKind = 'ui-slot' | 'data-table' | 'form' | 'component'

export interface WebExtensionPoint {
  id: string
  description: string
  kind: ExtensionPointKind
}

export interface WebExtensionProps {
  context: Readonly<Record<string, unknown>>
}

export interface WebExtension {
  id: string
  point: string
  order: number
  requiredPermission?: string
  component: ComponentType<WebExtensionProps> | LazyExoticComponent<ComponentType<WebExtensionProps>>
}

export interface ArchiveLifecycle {
  status: 'Active' | 'Archived' | 'Deleted'
  archivedAt?: string | null
  archivedBy?: string | null
  deletedAt?: string | null
  deletedBy?: string | null
  deletionReason?: string | null
}

export interface ArchiveItem {
  id: string
  title: string
  description: string
  lifecycle: ArchiveLifecycle
  version?: string
}

export interface ArchiveResourceContribution {
  kind: string
  typeLabel: string
  readPermission: string
  managePermission: string
  readFeature?: string
  manageFeature?: string
  load(): Promise<readonly ArchiveItem[]>
  restore(id: string, expectedVersion?: string): Promise<unknown>
  requestDeletion?(id: string, reason: string, expectedVersion?: string): Promise<unknown>
}

export interface ModuleFeatureDescriptor {
  key: string
  name: string
  description: string
  defaultEnabled: boolean
}

export interface InstalledModuleFeature {
  moduleId: string
  feature: ModuleFeatureDescriptor
}

export interface WebModule {
  id: string
  name: string
  version: string
  description: string
  requires: readonly string[]
  optionalDependencies: readonly string[]
  routes: readonly WebRoute[]
  navigation: readonly NavigationContribution[]
  extensionPoints: readonly WebExtensionPoint[]
  extensions: readonly WebExtension[]
  archiveResources?: readonly ArchiveResourceContribution[]
  tableContributions?: readonly WebTableContribution[]
  realtimeInvalidations?: readonly WebRealtimeInvalidationContribution[]
  features?: readonly ModuleFeatureDescriptor[]
}

export interface WebOverrides {
  routes?: Readonly<Record<string, WebRoute | null>>
  navigation?: Readonly<Record<string, NavigationContribution | null>>
  extensions?: Readonly<Record<string, WebExtension | null>>
  tableContributions?: Readonly<Record<string, WebTableContribution | null>>
}

const stableId = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/
const stableVersion = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$/
const stableContractId = /^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$/

export function defineWebModule<const T extends WebModule>(module: T): T {
  return module
}

export class WebModuleCatalog {
  readonly modules: readonly WebModule[]
  readonly routes: readonly WebRoute[]
  readonly navigation: readonly NavigationContribution[]
  readonly extensionPoints: readonly WebExtensionPoint[]
  readonly extensions: readonly WebExtension[]
  readonly archiveResources: readonly ArchiveResourceContribution[]
  readonly tableContributions: readonly WebTableContribution[]
  readonly realtimeInvalidations: readonly WebRealtimeInvalidationContribution[]
  readonly realtimeSubscriptions: readonly RealtimeAudience[]
  readonly features: readonly ModuleFeatureDescriptor[]

  constructor(
    modules: readonly WebModule[],
    overrides: WebOverrides = {},
    expectedFeatures?: readonly InstalledModuleFeature[],
  ) {
    validateModules(modules)
    this.modules = orderByDependencies(modules)
    this.features = this.modules.flatMap((module) => module.features ?? [])
    if (expectedFeatures !== undefined) validateExpectedFeatures(modules, expectedFeatures)
    this.extensionPoints = this.modules.flatMap((module) => module.extensionPoints)
    this.tableContributions = applyOverrides(this.modules.flatMap(module => module.tableContributions ?? []), overrides.tableContributions, 'table contribution')
      .toSorted((left, right) => left.order - right.order || left.id.localeCompare(right.id))
    ensureUnique(this.tableContributions.map(item => item.id), 'table contribution id')
    for (const item of this.tableContributions) {
      if (!stableContractId.test(item.id) || !Number.isFinite(item.order)
        || item.requiredPermission !== undefined && !stableContractId.test(item.requiredPermission))
        throw new Error(`Invalid Trykatch table contribution '${item.id}'.`)
      const point = this.extensionPoints.find(point => point.id === item.point.id)
      if (point !== item.point || point.kind !== 'data-table' || !hasValidTableBinding(item))
        throw new Error(`Trykatch table contribution '${item.id}' must target the exported data-table point object '${item.point.id}'.`)
    }
    this.routes = applyOverrides(this.modules.flatMap((module) => module.routes), overrides.routes, 'route')
    this.navigation = applyOverrides(this.modules.flatMap((module) => module.navigation), overrides.navigation, 'navigation contribution')
      .toSorted((left, right) => left.order - right.order || left.id.localeCompare(right.id))
    this.extensions = applyOverrides(this.modules.flatMap((module) => module.extensions), overrides.extensions, 'extension')
      .toSorted((left, right) => left.order - right.order || left.id.localeCompare(right.id))
    this.archiveResources = this.modules.flatMap((module) => module.archiveResources ?? [])
    ensureUnique(this.archiveResources.map((resource) => resource.kind), 'archive resource kind')
    for (const resource of this.archiveResources) {
      if (!stableId.test(resource.kind) || !resource.typeLabel.trim()) throw new Error(`Invalid Trykatch archive resource '${resource.kind}'.`)
      if (!stableContractId.test(resource.readPermission) || !stableContractId.test(resource.managePermission)) {
        throw new Error(`Trykatch archive resource '${resource.kind}' requires valid read and manage permissions.`)
      }
    }
    this.realtimeInvalidations = this.modules.flatMap((module) => module.realtimeInvalidations ?? [])
      .toSorted((left, right) => left.id.localeCompare(right.id))
    ensureUnique(this.realtimeInvalidations.map((item) => item.id), 'realtime invalidation contribution id')
    for (const item of this.realtimeInvalidations) {
      if (!stableContractId.test(item.id) || typeof item.matches !== 'function' || !item.queryKeys.length
        || item.queryKeys.some((key) => !Array.isArray(key) || !key.length))
        throw new Error(`Invalid Trykatch realtime invalidation contribution '${item.id}'.`)
      validateRealtimeAudience(item.id, item.subscription)
    }
    this.realtimeSubscriptions = this.realtimeInvalidations
      .map((item) => item.subscription)
      .filter((subscription, index, all) => all.findIndex((candidate) => sameRealtimeAudience(candidate, subscription)) === index)
    validateEffectiveContracts(this.routes, this.navigation, this.extensionPoints, this.extensions)
    validateFeatureBindings(this.routes, this.navigation, this.archiveResources, this.features)
  }

  extensionsFor(point: string) {
    return this.extensions.filter((extension) => extension.point === point)
  }

  tableFor<Row>(point: TableExtensionPoint<Row>, permissions: readonly string[], base: TableParts<Row> = {}) {
    if (!this.extensionPoints.includes(point)) throw new Error(`Unknown Trykatch table point '${point.id}'.`)
    return resolveTableContributions(point, this.tableContributions, permissions, base)
  }

  routesFor(surface: 'workspace' | 'platform') {
    return this.routes.filter((route) => (route.surface ?? 'workspace') === surface)
  }

  navigationFor(surface: 'workspace' | 'platform') {
    return this.navigation.filter((item) => (item.surface ?? 'workspace') === surface)
  }
}

function validateRealtimeAudience(contributionId: string, audience: RealtimeAudience) {
  if (!audience || !audience.policy || !stableId.test(audience.policy.ownerModuleId)
    || !stableContractId.test(audience.policy.policyKey) || typeof audience.policy.policyVersion !== 'number'
    || !Number.isSafeInteger(audience.policy.policyVersion) || audience.policy.policyVersion < 1 || ![0, 1, 2].includes(audience.kind))
    throw new Error(`Trykatch realtime contribution '${contributionId}' has an invalid subscription.`)
  if (audience.kind === 0 && audience.userId != null || audience.kind === 1 && !audience.userId
    || audience.kind === 2 && (audience.userId != null || !audience.resource?.resourceKey || !audience.resource.resourceId))
    throw new Error(`Trykatch realtime contribution '${contributionId}' has an invalid audience selector.`)
}

function sameRealtimeAudience(left: RealtimeAudience, right: RealtimeAudience) {
  return left.policy.ownerModuleId === right.policy.ownerModuleId && left.policy.policyKey === right.policy.policyKey
    && left.policy.policyVersion === right.policy.policyVersion && left.kind === right.kind
    && left.userId === right.userId && left.resource?.resourceKey === right.resource?.resourceKey
    && left.resource?.resourceId === right.resource?.resourceId
}

function validateModules(modules: readonly WebModule[]) {
  ensureUnique(modules.map((module) => module.id), 'module id')
  const installed = new Set(modules.map((module) => module.id))

  for (const module of modules) {
    if (!stableId.test(module.id) || module.id.length > 80) throw new Error(`Invalid Trykatch web module id '${module.id}'.`)
    if (!stableVersion.test(module.version)) throw new Error(`Trykatch web module '${module.id}' has invalid semantic version '${module.version}'.`)
    if (!module.name.trim() || !module.description.trim()) throw new Error(`Trykatch web module '${module.id}' requires a name and description.`)
    ensureUnique(module.requires, `dependency in '${module.id}'`)
    ensureUnique(module.optionalDependencies, `optional dependency in '${module.id}'`)
    for (const dependency of [...module.requires, ...module.optionalDependencies]) {
      if (!stableId.test(dependency)) throw new Error(`Trykatch web module '${module.id}' declares invalid dependency id '${dependency}'.`)
    }
    const overlap = module.requires.find((dependency) => module.optionalDependencies.includes(dependency))
    if (overlap) throw new Error(`Trykatch web module '${module.id}' declares '${overlap}' as both required and optional.`)
    if (module.requires.includes(module.id) || module.optionalDependencies.includes(module.id)) throw new Error(`Trykatch web module '${module.id}' cannot depend on itself.`)
    for (const dependency of module.requires) {
      if (!installed.has(dependency)) throw new Error(`Trykatch web module '${module.id}' requires missing module '${dependency}'.`)
    }
    validateModuleFeatures(module)
  }

  validateEffectiveContracts(
    modules.flatMap((module) => module.routes),
    modules.flatMap((module) => module.navigation),
    modules.flatMap((module) => module.extensionPoints),
    modules.flatMap((module) => module.extensions),
  )
  validateFeatureBindings(
    modules.flatMap((module) => module.routes),
    modules.flatMap((module) => module.navigation),
    modules.flatMap((module) => module.archiveResources ?? []),
    modules.flatMap((module) => module.features ?? []),
  )
}

function validateModuleFeatures(module: WebModule) {
  const features: unknown = module.features
  if (features === undefined) return
  if (!Array.isArray(features) || features.length > 100) {
    throw new Error(`Trykatch web module '${module.id}' must declare at most 100 features in an array.`)
  }

  for (const feature of features) validateFeatureDescriptor(module.id, feature)
  ensureUnique(features.map((feature: ModuleFeatureDescriptor) => feature.key), `feature key in '${module.id}'`)
}

function validateFeatureDescriptor(moduleId: string, value: unknown): asserts value is ModuleFeatureDescriptor {
  const subject = `Trykatch feature in module '${moduleId}'`
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${subject} must be an object.`)

  const descriptor = value as Record<string, unknown>
  const allowedProperties = new Set(['key', 'name', 'description', 'defaultEnabled'])
  if (Object.keys(descriptor).some((property) => !allowedProperties.has(property))) {
    throw new Error(`${subject} has an unknown property.`)
  }
  if (typeof descriptor.key !== 'string' || descriptor.key.length > 120
    || !stableContractId.test(descriptor.key) || !descriptor.key.startsWith(`${moduleId}.`)) {
    throw new Error(`${subject} has an invalid or foreign key.`)
  }
  if (typeof descriptor.name !== 'string' || !descriptor.name.trim() || descriptor.name.length > 160) {
    throw new Error(`${subject} has an invalid name.`)
  }
  if (typeof descriptor.description !== 'string' || !descriptor.description.trim() || descriptor.description.length > 1000) {
    throw new Error(`${subject} has an invalid description.`)
  }
  if (typeof descriptor.defaultEnabled !== 'boolean') throw new Error(`${subject} requires an explicit boolean default.`)
}

function validateExpectedFeatures(modules: readonly WebModule[], expectedFeatures: readonly InstalledModuleFeature[]) {
  if (!Array.isArray(expectedFeatures)) throw new Error('Expected Trykatch features must be an array.')

  const moduleIds = new Set(modules.map((module) => module.id))
  const expectedByKey = new Map<string, ModuleFeatureDescriptor>()
  const expectedCounts = new Map<string, number>()
  for (const value of expectedFeatures as readonly unknown[]) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      throw new Error('Expected Trykatch feature entry must be an object.')
    }
    const installedFeature = value as Record<string, unknown>
    if (Object.keys(installedFeature).some((property) => property !== 'moduleId' && property !== 'feature')) {
      throw new Error('Expected Trykatch feature entry has an unknown property.')
    }
    if (typeof installedFeature.moduleId !== 'string' || !moduleIds.has(installedFeature.moduleId)) {
      throw new Error(`Expected Trykatch feature references unknown module '${String(installedFeature.moduleId)}'.`)
    }

    const moduleId = installedFeature.moduleId
    const count = (expectedCounts.get(moduleId) ?? 0) + 1
    if (count > 100) throw new Error(`Expected Trykatch module '${moduleId}' declares more than 100 features.`)
    expectedCounts.set(moduleId, count)

    validateFeatureDescriptor(moduleId, installedFeature.feature)
    const feature = installedFeature.feature
    if (expectedByKey.has(feature.key)) throw new Error(`Duplicate expected Trykatch feature '${feature.key}'.`)
    expectedByKey.set(feature.key, feature)
  }

  const actualByKey = new Map<string, ModuleFeatureDescriptor>()
  for (const module of modules) {
    for (const feature of module.features ?? []) actualByKey.set(feature.key, feature)
  }
  if (actualByKey.size !== expectedByKey.size) throw new Error('Trykatch feature declarations do not match the expected manifest.')

  for (const [key, actual] of actualByKey) {
    const expected = expectedByKey.get(key)
    if (!expected || actual.name !== expected.name || actual.description !== expected.description
      || actual.defaultEnabled !== expected.defaultEnabled) {
      throw new Error(`Trykatch feature '${key}' does not match the expected manifest.`)
    }
  }
}

function validateEffectiveContracts(
  routes: readonly WebRoute[],
  navigation: readonly NavigationContribution[],
  extensionPoints: readonly WebExtensionPoint[],
  extensions: readonly WebExtension[],
) {
  ensureUnique(routes.map((route) => route.id), 'route id')
  ensureUnique(routes.map((route) => route.path), 'route path')
  ensureUnique(navigation.map((item) => item.id), 'navigation contribution id')
  ensureUnique(extensionPoints.map((point) => point.id), 'extension point id')
  ensureUnique(extensions.map((extension) => extension.id), 'extension id')

  const declaredPoints = new Set(extensionPoints.map((point) => point.id))
  for (const point of extensionPoints) {
    if (!stableContractId.test(point.id) || !point.description.trim()) throw new Error(`Invalid Trykatch extension point '${point.id}'.`)
  }
  for (const extension of extensions) {
    if (!stableContractId.test(extension.id)) throw new Error(`Invalid Trykatch extension id '${extension.id}'.`)
    if (!declaredPoints.has(extension.point)) throw new Error(`Trykatch extension '${extension.id}' targets unknown point '${extension.point}'.`)
  }
}

function validateFeatureBindings(
  routes: readonly WebRoute[],
  navigation: readonly NavigationContribution[],
  archiveResources: readonly ArchiveResourceContribution[],
  installedFeatures: readonly ModuleFeatureDescriptor[],
) {
  const featureKeys = new Set(installedFeatures.map((feature) => feature.key))
  const validateFeature = (featureKey: string | undefined, subject: string) => {
    if (featureKey !== undefined && (typeof featureKey !== 'string' || !featureKeys.has(featureKey))) {
      throw new Error(`${subject} references an unknown installed Trykatch feature '${String(featureKey)}'.`)
    }
  }

  for (const route of routes) {
    if (route.requiredPermission !== undefined && !stableContractId.test(route.requiredPermission)) {
      throw new Error(`Trykatch route '${route.id}' has an invalid required permission.`)
    }
    validateFeature(route.requiredFeature, `Trykatch route '${route.id}'`)
    if ((route.surface ?? 'workspace') === 'platform' && route.requiredFeature !== undefined) {
      throw new Error(`Trykatch platform route '${route.id}' cannot require an organization feature.`)
    }
  }
  for (const item of navigation) {
    validateFeature(item.requiredFeature, `Trykatch navigation contribution '${item.id}'`)
    if ((item.surface ?? 'workspace') === 'platform' && item.requiredFeature !== undefined) {
      throw new Error(`Trykatch platform navigation contribution '${item.id}' cannot require an organization feature.`)
    }
  }
  for (const resource of archiveResources) {
    validateFeature(resource.readFeature, `Trykatch archive resource '${resource.kind}' read access`)
    validateFeature(resource.manageFeature, `Trykatch archive resource '${resource.kind}' manage access`)
  }
}

function applyOverrides<T extends { id: string }>(
  contracts: readonly T[],
  overrides: Readonly<Record<string, T | null>> | undefined,
  subject: string,
) {
  if (!overrides) return [...contracts]
  const known = new Set(contracts.map((contract) => contract.id))
  for (const [id, replacement] of Object.entries(overrides)) {
    if (!known.has(id)) throw new Error(`Cannot override unknown Trykatch ${subject} '${id}'.`)
    if (replacement && replacement.id !== id) throw new Error(`Trykatch ${subject} override '${id}' must preserve its stable id.`)
  }
  return contracts.flatMap((contract) => {
    const replacement = overrides[contract.id]
    if (replacement === undefined) return [contract]
    return replacement === null ? [] : [replacement]
  })
}

function orderByDependencies(modules: readonly WebModule[]) {
  const byId = new Map(modules.map((module) => [module.id, module]))
  const ordered: WebModule[] = []
  const visiting = new Set<string>()
  const visited = new Set<string>()

  function visit(id: string) {
    if (visited.has(id)) return
    if (visiting.has(id)) throw new Error(`Trykatch web module dependency cycle includes '${id}'.`)
    visiting.add(id)
    const module = byId.get(id)
    if (!module) return
    const dependencies = [...module.requires, ...module.optionalDependencies.filter((dependency) => byId.has(dependency))]
    for (const dependency of dependencies.toSorted()) visit(dependency)
    visiting.delete(id)
    visited.add(id)
    ordered.push(module)
  }

  for (const id of [...byId.keys()].toSorted()) visit(id)
  return ordered
}

function ensureUnique(values: readonly string[], subject: string) {
  const seen = new Set<string>()
  for (const value of values) {
    if (seen.has(value)) throw new Error(`Duplicate Trykatch ${subject} '${value}'.`)
    seen.add(value)
  }
}
