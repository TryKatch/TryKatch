import { describe, expect, it } from 'vitest'
import { __MODULE_CAMEL__Module, __MODULE_CAMEL__Table } from './index'

describe('__MODULE_CAMEL__Module', () => {
  it('declares the generated route and permission', () => {
    expect(__MODULE_CAMEL__Module.id).toBe('__MODULE_ID__')
    expect(__MODULE_CAMEL__Module.extensionPoints[0]).toBe(__MODULE_CAMEL__Table)
    expect(__MODULE_CAMEL__Module.routes[0]?.path).toBe('/__RESOURCE__')
    expect(__MODULE_CAMEL__Module.navigation[0]?.requiredPermission).toBe('__MODULE_ID__.read')
    expect(__MODULE_CAMEL__Module.archiveResources?.[0]?.managePermission).toBe('__MODULE_ID__.manage')
  })
})
