import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => { vi.unstubAllEnvs(); vi.resetModules() })

describe('application branding', () => {
  it('allows a display brand independent of the technical namespace', async () => {
    vi.stubEnv('VITE_APPLICATION_NAME', '  Kamenta & partners  ')
    expect((await import('./branding')).applicationName).toBe('Kamenta & partners')
  })
  it('uses the generated display brand when the override is blank', async () => {
    vi.stubEnv('VITE_APPLICATION_NAME', '   ')
    expect((await import('./branding')).applicationName).toBe("Trykatch Product")
  })
})
