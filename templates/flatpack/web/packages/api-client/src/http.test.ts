import { afterEach, describe, expect, it, vi } from 'vitest'

import { customFetch } from './http'

describe('customFetch', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('lets the API infer workspace scope from the protected cookie', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ items: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await customFetch('/api/v1/projects', { method: 'GET' })

    expect(fetchMock).toHaveBeenCalledOnce()
    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(new Headers(options.headers).has('X-Organization')).toBe(false)
    expect(options.credentials).toBe('include')
  })

  it('does not manufacture a tenant header for neutral resource requests', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ items: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await customFetch('/api/v1/projects', {
      method: 'GET',
      headers: { Accept: 'application/json' },
    })

    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(new Headers(options.headers).has('X-Organization')).toBe(false)
    expect(options.credentials).toBe('include')
  })
})
