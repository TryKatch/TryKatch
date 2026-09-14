import { describe, expect, it } from 'vitest'
import { __MODULE_CAMEL__Module } from './index'

describe('__MODULE_CAMEL__Module', () => {
  it('declares the generated route and permission', () => {
    expect(__MODULE_CAMEL__Module.id).toBe('__MODULE_ID__')
    expect(__MODULE_CAMEL__Module.routes[0]?.path).toBe('/__RESOURCE__')
    expect(__MODULE_CAMEL__Module.navigation[0]?.requiredPermission).toBe('__MODULE_ID__.read')
    expect(__MODULE_CAMEL__Module.archiveResources?.[0]?.managePermission).toBe('__MODULE_ID__.manage')
  })
})
