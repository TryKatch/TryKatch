import { afterEach, describe, expect, it, vi } from 'vitest'
import { documentsUpdate, documentsUpload } from './generated/client'
import { setAntiforgeryToken } from './http'

describe('generated Documents client', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('sends typed title and description updates to the document resource', async () => {
    setAntiforgeryToken('csrf-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'document-id' }), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    await documentsUpdate('document-id', { title: 'Updated', description: 'New description' })

    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/v1/documents/document-id')
    expect(options.method).toBe('PUT')
    expect(JSON.parse(options.body as string)).toEqual({ title: 'Updated', description: 'New description' })
  })

  it('sends upload fields as multipart form data without overriding its boundary', async () => {
    setAntiforgeryToken('csrf-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'document-id' }), {
      status: 201, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    const file = new File(['runbook'], 'recovery-runbook.pdf', { type: 'application/pdf' })

    await documentsUpload({ title: 'Runbook', description: 'Recovery steps', file })

    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = options.body as FormData
    expect(options.method).toBe('POST')
    expect(body).toBeInstanceOf(FormData)
    expect(body.get('title')).toBe('Runbook')
    expect(body.get('description')).toBe('Recovery steps')
    const uploadedFile = body.get('file') as File
    expect(uploadedFile).toBeInstanceOf(File)
    expect(uploadedFile.name).toBe('recovery-runbook.pdf')
    expect(uploadedFile.size).toBe(file.size)
    expect(uploadedFile.type).toBe('application/pdf')
    expect(new Headers(options.headers).has('Content-Type')).toBe(false)
  })
})
