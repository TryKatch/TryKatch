import { createContext, createElement, useContext, type ReactNode } from 'react'
import type { WebModuleCatalog } from './index'

export interface OrganizationFeatureSnapshot {
  organizationId: string
  values: Readonly<Record<string, boolean>>
  evaluatedAtUtc?: string
}

export interface FeatureAvailabilityState {
  organizationId?: string
  snapshot?: OrganizationFeatureSnapshot
  status: 'loading' | 'success' | 'error'
}

interface ModuleFeatureContextValue {
  catalog: WebModuleCatalog
  state: FeatureAvailabilityState
}

const ModuleFeatureContext = createContext<ModuleFeatureContextValue | null>(null)

export function isModuleFeatureEnabled(catalog: WebModuleCatalog, state: FeatureAvailabilityState, key: string): boolean {
  if (!catalog || !Array.isArray(catalog.features) || typeof key !== 'string' || key.length === 0
    || !state || state.status !== 'success' || !isNonEmptyString(state.organizationId)) return false

  const snapshot = state.snapshot
  if (!snapshot || !isNonEmptyString(snapshot.organizationId) || snapshot.organizationId !== state.organizationId
    || !snapshot.values || typeof snapshot.values !== 'object' || Array.isArray(snapshot.values)) return false

  if (!catalog.features.some((feature) => feature?.key === key)) return false
  return Object.hasOwn(snapshot.values, key) && snapshot.values[key] === true
}

export function ModuleFeatureProvider({ catalog, state, children }: { catalog: WebModuleCatalog; state: FeatureAvailabilityState; children: ReactNode }) {
  return createElement(ModuleFeatureContext.Provider, { value: { catalog, state }, children })
}

export function useModuleFeature(key: string): boolean {
  const value = useContext(ModuleFeatureContext)
  return value ? isModuleFeatureEnabled(value.catalog, value.state, key) : false
}

export function useModuleFeatures(): { organizationId?: string; status: 'loading' | 'success' | 'error'; isEnabled: (key: string) => boolean } {
  const value = useContext(ModuleFeatureContext)
  if (!value) return { status: 'loading', isEnabled: () => false }

  const { catalog, state } = value
  if (!state || typeof state !== 'object') return { status: 'loading', isEnabled: () => false }
  return {
    ...(isNonEmptyString(state.organizationId) ? { organizationId: state.organizationId } : {}),
    status: state.status === 'success' || state.status === 'error' ? state.status : 'loading',
    isEnabled: (key) => isModuleFeatureEnabled(catalog, state, key),
  }
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
