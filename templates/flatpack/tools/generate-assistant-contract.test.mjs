import assert from 'node:assert/strict'
import test from 'node:test'
import { buildAssistantContract } from './generate-assistant-contract.mjs'

function specification(method = 'get', declaration = { name: 'flatpack_list_things', description: 'List things.', risk: 'read-only', requiresHumanConfirmation: false }) {
  return {
    openapi: '3.1.1',
    info: { title: 'Test API', version: 'v1' },
    'x-flatpack-modules': [{ id: 'things', version: '1.0.0' }],
    paths: {
      '/api/v1/things': {
        [method]: {
          operationId: 'Things_List',
          'x-flatpack-module': 'things',
          'x-flatpack-assistant-tool': declaration,
          parameters: [{ name: 'search', in: 'query', schema: { type: 'string' } }],
        },
      },
    },
  }
}

test('exports strict tools and request bindings from explicit OpenAPI metadata', () => {
  const contract = buildAssistantContract(specification())

  assert.equal(contract.tools[0].name, 'flatpack_list_things')
  assert.deepEqual(contract.tools[0].parameters.required, ['search'])
  assert.deepEqual(contract.tools[0].parameters.properties.search.anyOf.at(-1), { type: 'null' })
  assert.equal(contract.bindings.flatpack_list_things.moduleId, 'things')
  assert.equal(contract.bindings.flatpack_list_things.authorization, 'api-enforced')
})

test('rejects a state-changing operation labelled read-only', () => {
  assert.throws(() => buildAssistantContract(specification('post')), /must target a GET operation/)
})

test('rejects mutating tools without human confirmation', () => {
  const declaration = { name: 'flatpack_create_thing', description: 'Create a thing.', risk: 'mutating', requiresHumanConfirmation: false }
  assert.throws(() => buildAssistantContract(specification('post', declaration)), /must require human confirmation/)
})
