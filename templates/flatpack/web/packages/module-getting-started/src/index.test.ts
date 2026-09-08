import { describe, expect, it } from 'vitest'
import { gettingStartedModule } from './index'

describe('gettingStartedModule', () => {
  it('declares the same stable identity and dependency as its package manifest', () => {
    expect(gettingStartedModule.id).toBe('getting-started')
    expect(gettingStartedModule.version).toBe('1.0.0')
    expect(gettingStartedModule.requires).toEqual(['projects'])
    expect(gettingStartedModule.extensions[0]?.point).toBe('projects.list.after-table')
  })
})
