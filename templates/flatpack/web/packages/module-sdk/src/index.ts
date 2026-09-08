import type { ComponentType, LazyExoticComponent } from 'react'

export interface FlatpackModuleIconProps {
  size?: string | number
}

export interface FlatpackWebRoute {
  id: string
  path: `/${string}`
  component: ComponentType | LazyExoticComponent<ComponentType>
}

export interface FlatpackNavigationContribution {
  id: string
  section: string
  order: number
  to: `/${string}`
  label: string
  icon: ComponentType<FlatpackModuleIconProps>
  requiredPermission?: string
  exact?: boolean
}

export interface FlatpackWebModule {
  id: string
  name: string
  version: string
  description: string
  requires: readonly string[]
  optionalDependencies: readonly string[]
  routes: readonly FlatpackWebRoute[]
  navigation: readonly FlatpackNavigationContribution[]
}

const stableId = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/
const stableVersion = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$/

export function defineFlatpackWebModule<const T extends FlatpackWebModule>(module: T): T {
  return module
}

export class FlatpackWebModuleCatalog {
  readonly modules: readonly FlatpackWebModule[]
  readonly routes: readonly FlatpackWebRoute[]
  readonly navigation: readonly FlatpackNavigationContribution[]

  constructor(modules: readonly FlatpackWebModule[]) {
    validateModules(modules)
    this.modules = orderByDependencies(modules)
    this.routes = this.modules.flatMap((module) => module.routes)
    this.navigation = this.modules
      .flatMap((module) => module.navigation)
      .toSorted((left, right) => left.order - right.order || left.id.localeCompare(right.id))
  }
}

function validateModules(modules: readonly FlatpackWebModule[]) {
  ensureUnique(modules.map((module) => module.id), 'module id')
  const installed = new Set(modules.map((module) => module.id))

  for (const module of modules) {
    if (!stableId.test(module.id) || module.id.length > 80) throw new Error(`Invalid Flatpack web module id '${module.id}'.`)
    if (!stableVersion.test(module.version)) throw new Error(`Flatpack web module '${module.id}' has invalid semantic version '${module.version}'.`)
    if (!module.name.trim() || !module.description.trim()) throw new Error(`Flatpack web module '${module.id}' requires a name and description.`)
    ensureUnique(module.requires, `dependency in '${module.id}'`)
    ensureUnique(module.optionalDependencies, `optional dependency in '${module.id}'`)
    for (const dependency of [...module.requires, ...module.optionalDependencies]) {
      if (!stableId.test(dependency)) throw new Error(`Flatpack web module '${module.id}' declares invalid dependency id '${dependency}'.`)
    }
    const overlap = module.requires.find((dependency) => module.optionalDependencies.includes(dependency))
    if (overlap) throw new Error(`Flatpack web module '${module.id}' declares '${overlap}' as both required and optional.`)
    if (module.requires.includes(module.id) || module.optionalDependencies.includes(module.id)) throw new Error(`Flatpack web module '${module.id}' cannot depend on itself.`)
    for (const dependency of module.requires) {
      if (!installed.has(dependency)) throw new Error(`Flatpack web module '${module.id}' requires missing module '${dependency}'.`)
    }
  }

  ensureUnique(modules.flatMap((module) => module.routes.map((route) => route.id)), 'route id')
  ensureUnique(modules.flatMap((module) => module.routes.map((route) => route.path)), 'route path')
  ensureUnique(modules.flatMap((module) => module.navigation.map((item) => item.id)), 'navigation contribution id')
}

function orderByDependencies(modules: readonly FlatpackWebModule[]) {
  const byId = new Map(modules.map((module) => [module.id, module]))
  const ordered: FlatpackWebModule[] = []
  const visiting = new Set<string>()
  const visited = new Set<string>()

  function visit(id: string) {
    if (visited.has(id)) return
    if (visiting.has(id)) throw new Error(`Flatpack web module dependency cycle includes '${id}'.`)
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
    if (seen.has(value)) throw new Error(`Duplicate Flatpack ${subject} '${value}'.`)
    seen.add(value)
  }
}
