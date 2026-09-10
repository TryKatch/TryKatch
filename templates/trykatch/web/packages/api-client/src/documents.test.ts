import { afterEach, describe, expect, it, vi } from 'vitest'
import { documentsUpdate } from './generated/trykatch'
import { setAntiforgeryToken } from './http'

describe('generated Documents client', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('sends typed title and content updates to the document resource', async () => {
    setAntiforgeryToken('csrf-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'document-id' }), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    await documentsUpdate('document-id', { title: 'Updated', content: 'New content' })

    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/v1/documents/document-id')
    expect(options.method).toBe('PUT')
    expect(JSON.parse(options.body as string)).toEqual({ title: 'Updated', content: 'New content' })
  })
})
