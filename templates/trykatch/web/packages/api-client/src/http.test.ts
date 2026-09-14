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

  it('returns binary download responses as blobs', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(new Uint8Array([1, 2, 3]), {
        status: 200,
        headers: { 'Content-Type': 'application/octet-stream' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await customFetch<Blob>('/api/v1/documents/document-id/content', { method: 'GET' })

    expect(result).toBeInstanceOf(Blob)
    expect(result.size).toBe(3)
  })
})
