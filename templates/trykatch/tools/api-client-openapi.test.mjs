import assert from 'node:assert/strict'
import { readdir, readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

const templateRoot = fileURLToPath(new URL('../', import.meta.url))
const openApiDirectory = join(templateRoot, 'web/packages/api-client/openapi')
const generatedClientPath = join(templateRoot, 'web/packages/api-client/src/generated/client.ts')
const generatedModelsIndexPath = join(templateRoot, 'web/packages/api-client/src/generated/models/index.ts')

test('generated optional Documents client and model match the built OpenAPI document', async () => {
  const openApiFiles = (await readdir(openApiDirectory)).filter((file) => file.endsWith('.json'))
  assert.equal(openApiFiles.length, 1, 'Expected exactly one generated OpenAPI document.')

  const [openApiText, generatedClient, generatedModelsIndex] = await Promise.all([
    readFile(join(openApiDirectory, openApiFiles[0]), 'utf8'),
    readFile(generatedClientPath, 'utf8'),
    readFile(generatedModelsIndexPath, 'utf8'),
  ])
  const openApi = JSON.parse(openApiText)
  const operationIds = new Set(Object.values(openApi.paths ?? {}).flatMap((path) =>
    Object.values(path).map((operation) => operation.operationId).filter(Boolean)))
  const hasUpload = operationIds.has('Documents_Upload')
  const hasUpdate = operationIds.has('Documents_Update')
  const hasDocumentDto = Object.hasOwn(openApi.components?.schemas ?? {}, 'DocumentDto')

  assert.equal(hasUpload, generatedClient.includes('export const documentsUpload'))
  assert.equal(hasUpdate, generatedClient.includes('export const documentsUpdate'))
  assert.equal(hasDocumentDto, generatedModelsIndex.includes("export * from './documentDto'"))
})
