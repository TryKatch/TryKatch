import { readFile, writeFile, mkdir } from 'node:fs/promises'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const templateRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const defaultOpenApiPath = path.join(templateRoot, 'web/packages/api-client/openapi/FlatpackApp.Api.json')
const defaultOutputPath = path.join(templateRoot, 'docs/generated/assistant-contract.json')
const httpMethods = new Set(['get', 'post', 'put', 'patch', 'delete'])

export function buildAssistantContract(openApi) {
  const operations = new Map()
  for (const [route, pathItem] of Object.entries(openApi.paths ?? {})) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (!httpMethods.has(method) || !operation?.operationId) continue
      if (operations.has(operation.operationId)) throw new Error(`Duplicate OpenAPI operation id '${operation.operationId}'.`)
      operations.set(operation.operationId, { route, method: method.toUpperCase(), operation })
    }
  }

  const tools = []
  const bindings = {}
  for (const [operationId, entry] of operations) {
    const declaration = entry.operation['x-flatpack-assistant-tool']
    if (!declaration) continue

    validateDeclaration(declaration, operationId, entry.method)
    const parameters = buildStrictParameters(entry.operation.parameters ?? [])
    tools.push({
      type: 'function',
      name: declaration.name,
      description: declaration.description,
      strict: true,
      parameters,
    })
    bindings[declaration.name] = {
      operationId,
      method: entry.method,
      path: entry.route,
      moduleId: entry.operation['x-flatpack-module'],
      risk: declaration.risk,
      requiresHumanConfirmation: declaration.requiresHumanConfirmation,
      authorization: 'api-enforced',
      organizationContext: 'request-derived',
    }
  }

  tools.sort((left, right) => left.name.localeCompare(right.name))
  const orderedBindings = Object.fromEntries(Object.entries(bindings).sort(([left], [right]) => left.localeCompare(right)))
  return {
    schemaVersion: 1,
    source: {
      openapi: openApi.openapi,
      title: openApi.info?.title,
      version: openApi.info?.version,
    },
    modules: openApi['x-flatpack-modules'] ?? [],
    policy: {
      default: 'deny',
      authorization: 'The API re-authorizes every tool call; the model never grants access.',
      writes: 'Mutating and destructive tools require an explicit allowlist entry and human confirmation.',
    },
    tools,
    bindings: orderedBindings,
  }
}

function validateDeclaration(declaration, operationId, method) {
  if (!/^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$/.test(declaration.name ?? '')) {
    throw new Error(`Assistant tool on '${operationId}' has an invalid name.`)
  }
  if (!declaration.description?.trim()) throw new Error(`Assistant tool '${declaration.name}' requires a description.`)
  if (!['read-only', 'mutating', 'destructive'].includes(declaration.risk)) {
    throw new Error(`Assistant tool '${declaration.name}' has invalid risk '${declaration.risk}'.`)
  }
  if (declaration.risk === 'read-only' && method !== 'GET') {
    throw new Error(`Read-only assistant tool '${declaration.name}' must target a GET operation.`)
  }
  if (declaration.risk !== 'read-only' && declaration.requiresHumanConfirmation !== true) {
    throw new Error(`State-changing assistant tool '${declaration.name}' must require human confirmation.`)
  }
}

function buildStrictParameters(parameters) {
  const properties = {}
  const required = []
  for (const parameter of parameters) {
    if (!['path', 'query'].includes(parameter.in)) continue
    const isRequired = parameter.required === true || parameter.in === 'path'
    const schema = normalizeSchema(parameter.schema ?? { type: 'string' })
    properties[parameter.name] = isRequired ? schema : { anyOf: [schema, { type: 'null' }] }
    required.push(parameter.name)
  }
  return { type: 'object', properties, required, additionalProperties: false }
}

function normalizeSchema(schema) {
  const normalized = {}
  if (Array.isArray(schema.type)) {
    const preferred = schema.type.find((type) => type !== 'string' && type !== 'null')
      ?? schema.type.find((type) => type !== 'null')
      ?? 'string'
    normalized.type = preferred
  } else {
    normalized.type = schema.type ?? 'string'
  }
  for (const property of ['description', 'enum']) {
    if (schema[property] !== undefined) normalized[property] = schema[property]
  }
  if (normalized.type === 'string') {
    for (const property of ['format', 'minLength', 'maxLength', 'pattern']) {
      if (schema[property] !== undefined) normalized[property] = schema[property]
    }
  }
  if (normalized.type === 'integer' || normalized.type === 'number') {
    for (const property of ['minimum', 'maximum']) {
      if (schema[property] !== undefined) normalized[property] = schema[property]
    }
  }
  if (normalized.type === 'array') normalized.items = normalizeSchema(schema.items ?? { type: 'string' })
  return normalized
}

export async function generate({ openApiPath = defaultOpenApiPath, outputPath = defaultOutputPath, check = false } = {}) {
  const openApi = JSON.parse(await readFile(openApiPath, 'utf8'))
  const output = `${JSON.stringify(buildAssistantContract(openApi), null, 2)}\n`
  if (check) {
    const existing = await readFile(outputPath, 'utf8').catch(() => '')
    if (existing !== output) throw new Error(`Generated assistant contract is stale. Run 'pnpm generate'.`)
    return
  }
  await mkdir(path.dirname(outputPath), { recursive: true })
  await writeFile(outputPath, output)
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  generate({ check: process.argv.includes('--check') }).catch((error) => {
    console.error(error.message)
    process.exitCode = 1
  })
}
