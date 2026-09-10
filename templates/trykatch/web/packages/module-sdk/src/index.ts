import type { ComponentType, LazyExoticComponent } from 'react'

export { ModuleExtensionSlot, ModuleProvider } from './extensions'

export interface ModuleIconProps {
  size?: string | number
}

export interface WebRoute {
  id: string
  path: `/${string}`
  surface?: 'workspace' | 'platform'
  component: ComponentType | LazyExoticComponent<ComponentType>
}

export interface NavigationContribution {
  id: string
  section: string
  order: number
  to: `/${string}`
  label: string
  icon: ComponentType<ModuleIconProps>
  requiredPermission?: string
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
}

export interface ArchiveResourceContribution {
  kind: string
  typeLabel: string
  readPermission: string
  managePermission: string
  load(): Promise<readonly ArchiveItem[]>
  restore(id: string): Promise<unknown>
  requestDeletion?(id: string, reason: string): Promise<unknown>
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
}

export interface WebOverrides {
  routes?: Readonly<Record<string, WebRoute | null>>
  navigation?: Readonly<Record<string, NavigationContribution | null>>
  extensions?: Readonly<Record<string, WebExtension | null>>
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

  constructor(modules: readonly WebModule[], overrides: WebOverrides = {}) {
    validateModules(modules)
    this.modules = orderByDependencies(modules)
    this.extensionPoints = this.modules.flatMap((module) => module.extensionPoints)
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
    validateEffectiveContracts(this.routes, this.navigation, this.extensionPoints, this.extensions)
  }

  extensionsFor(point: string) {
    return this.extensions.filter((extension) => extension.point === point)
  }

  routesFor(surface: 'workspace' | 'platform') {
    return this.routes.filter((route) => (route.surface ?? 'workspace') === surface)
  }

  navigationFor(surface: 'workspace' | 'platform') {
    return this.navigation.filter((item) => (item.surface ?? 'workspace') === surface)
  }
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
  }

  validateEffectiveContracts(
    modules.flatMap((module) => module.routes),
    modules.flatMap((module) => module.navigation),
    modules.flatMap((module) => module.extensionPoints),
    modules.flatMap((module) => module.extensions),
  )
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
