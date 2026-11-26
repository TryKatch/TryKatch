import { afterEach, describe, expect, it, vi } from 'vitest'
import * as generatedClient from './generated/client'
import { setAntiforgeryToken } from './http'

type GeneratedDocumentsClient = {
  documentsUpdate: (id: string, request: { title: string; description: string }) => Promise<unknown>
  documentsUpload: (request: { title: string; description: string; file: File; operationId?: string }) => Promise<unknown>
}

const generatedDocumentsClient = generatedClient as unknown as Partial<GeneratedDocumentsClient>

describe('optional generated Documents client', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('exposes document operations only when the module is installed', () => {
    expect(typeof generatedDocumentsClient.documentsUpdate).toBe(typeof generatedDocumentsClient.documentsUpload)
  })

  if (typeof generatedDocumentsClient.documentsUpdate === 'function') it('sends typed title and description updates to the document resource', async () => {
    setAntiforgeryToken('csrf-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'document-id' }), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    await generatedDocumentsClient.documentsUpdate!('document-id', { title: 'Updated', description: 'New description' })

    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/v1/documents/document-id')
    expect(options.method).toBe('PUT')
    expect(JSON.parse(options.body as string)).toEqual({ title: 'Updated', description: 'New description' })
  })

  if (typeof generatedDocumentsClient.documentsUpload === 'function') it.each([undefined, '11111111-1111-4111-8111-111111111111'])('sends upload fields and optional operation %s without overriding the multipart boundary', async (operationId) => {
    setAntiforgeryToken('csrf-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'document-id' }), {
      status: 201, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    const file = new File(['runbook'], 'recovery-runbook.pdf', { type: 'application/pdf' })

    await generatedDocumentsClient.documentsUpload!({ title: 'Runbook', description: 'Recovery steps', file, operationId })

    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = options.body as FormData
    expect(options.method).toBe('POST')
    expect(body).toBeInstanceOf(FormData)
    expect(body.get('title')).toBe('Runbook')
    expect(body.get('description')).toBe('Recovery steps')
    expect(body.get('operationId')).toBe(operationId ?? null)
    const uploadedFile = body.get('file') as File
    expect(uploadedFile).toBeInstanceOf(File)
    expect(uploadedFile.name).toBe('recovery-runbook.pdf')
    expect(uploadedFile.size).toBe(file.size)
    expect(uploadedFile.type).toBe('application/pdf')
    expect(new Headers(options.headers).has('Content-Type')).toBe(false)
  })
})
