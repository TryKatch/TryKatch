import assert from 'node:assert/strict'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { startStorageGate } from './quota-storage-gate.mjs'

async function listen(server) {
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  return server.address()
}

async function fixture(upstreamHandler) {
  const controlDirectory = await mkdtemp(path.join(os.tmpdir(), 'trykatch-storage-gate-'))
  const upstreamServer = http.createServer(upstreamHandler)
  const upstreamAddress = await listen(upstreamServer)
  const gate = await startStorageGate({
    upstream: `http://127.0.0.1:${upstreamAddress.port}`,
    controlDirectory,
    port: 0,
    host: '127.0.0.1',
  })
  const gateUrl = `http://127.0.0.1:${gate.address.port}`
  return {
    controlDirectory,
    gateUrl,
    async close() {
      await gate.close()
      await new Promise(resolve => upstreamServer.close(resolve))
      await rm(controlDirectory, { recursive: true, force: true })
    },
  }
}

function request(url, { method = 'PUT', path: requestPath = '/bucket/key?version=1', headers = {}, body = Buffer.alloc(0), signal } = {}) {
  const target = new URL(url)
  return new Promise((resolve, reject) => {
    const client = http.request({
      protocol: target.protocol,
      hostname: target.hostname,
      port: target.port,
      method,
      path: requestPath,
      headers,
      ...(headers.host ? { setHost: false } : {}),
    }, incoming => {
      const chunks = []
      incoming.on('data', chunk => chunks.push(chunk))
      incoming.once('error', reject)
      incoming.once('end', () => {
        const responseBody = Buffer.concat(chunks)
        resolve({
          status: incoming.statusCode,
          headers: { get: name => incoming.headers[name.toLowerCase()] ?? null },
          text: async () => responseBody.toString('utf8'),
        })
      })
    })
    client.once('error', reject)
    const abort = () => client.destroy(new Error('Request aborted by test'))
    signal?.addEventListener('abort', abort, { once: true })
    client.once('close', () => signal?.removeEventListener('abort', abort))
    if (signal?.aborted) abort()
    else if (body.length) client.end(body)
    else client.end()
  })
}

async function waitForFile(file, timeoutMs = 2000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try {
      await readFile(file)
      return
    } catch {
      await new Promise(resolve => setTimeout(resolve, 10))
    }
  }
  assert.fail(`Timed out waiting for ${path.basename(file)}`)
}

test('storage gate preserves raw request path, signed Host, headers, and all body bytes', async () => {
  const body = Buffer.from([0, 255, 1, 13, 10, 42, 128])
  let observed
  const current = await fixture(async (incoming, response) => {
    const chunks = []
    for await (const chunk of incoming) chunks.push(chunk)
    observed = {
      method: incoming.method,
      url: incoming.url,
      host: incoming.headers.host,
      authorization: incoming.headers.authorization,
      metadata: incoming.headers['x-amz-meta-note'],
      body: Buffer.concat(chunks),
    }
    response.writeHead(200, { 'content-type': 'application/octet-stream', 'x-storage-result': 'kept' })
    response.end('stored')
  })
  try {
    await writeFile(path.join(current.controlDirectory, 'arm'), '')
    const responsePromise = request(current.gateUrl, {
      path: '/bucket/a%2Fb?partNumber=2&uploadId=x%2By',
      headers: {
        host: 'signed-bucket.s3.example.test',
        authorization: 'AWS4-HMAC-SHA256 Credential=test/20261025/s3/aws4_request, Signature=abc',
        'if-none-match': '*',
        'x-amz-meta-note': 'retain me',
      },
      body,
    })
    await waitForFile(path.join(current.controlDirectory, 'put-persisted'))
    await writeFile(path.join(current.controlDirectory, 'release'), '')
    const response = await responsePromise
    assert.equal(response.status, 200)
    assert.equal(response.headers.get('x-storage-result'), 'kept')
    assert.equal(await response.text(), 'stored')
    assert.deepEqual(observed, {
      method: 'PUT',
      url: '/bucket/a%2Fb?partNumber=2&uploadId=x%2By',
      host: 'signed-bucket.s3.example.test',
      authorization: 'AWS4-HMAC-SHA256 Credential=test/20261025/s3/aws4_request, Signature=abc',
      metadata: 'retain me',
      body,
    })
    assert.equal(await readFile(path.join(current.controlDirectory, 'put-persisted'), 'utf8'), 'persisted')
  } finally {
    await current.close()
  }
})

test('successful armed conditional PUT waits for full upstream completion and explicit release', async () => {
  let finishUpstream
  const current = await fixture(async (_incoming, response) => {
    response.writeHead(200, { 'x-storage-result': 'real' })
    response.write('first-')
    await new Promise(resolve => { finishUpstream = resolve })
    response.end('last')
  })
  try {
    await writeFile(path.join(current.controlDirectory, 'arm'), '')
    const responsePromise = request(current.gateUrl, { headers: { 'if-none-match': '*' } })
    await new Promise(resolve => setTimeout(resolve, 40))
    assert.equal(await readFile(path.join(current.controlDirectory, 'put-persisted')).then(() => true, () => false), false)
    finishUpstream()
    await waitForFile(path.join(current.controlDirectory, 'put-persisted'))
    const early = await Promise.race([
      responsePromise.then(() => 'released'),
      new Promise(resolve => setTimeout(() => resolve('held'), 50)),
    ])
    assert.equal(early, 'held')
    await writeFile(path.join(current.controlDirectory, 'release'), '')
    const response = await responsePromise
    assert.equal(response.status, 200)
    assert.equal(response.headers.get('x-storage-result'), 'real')
    assert.equal(await response.text(), 'first-last')
  } finally {
    finishUpstream?.()
    await current.close()
  }
})

test('one arm gates only one of two successful conditional PUTs', async () => {
  const current = await fixture(async (_incoming, response) => {
    response.writeHead(200)
    response.end('stored')
  })
  try {
    await writeFile(path.join(current.controlDirectory, 'arm'), '')
    const first = request(current.gateUrl, { path: '/bucket/one', headers: { 'if-none-match': '*' } })
    const second = request(current.gateUrl, { path: '/bucket/two', headers: { 'if-none-match': '*' } })
    const either = await Promise.race([
      Promise.all([first, second]).then(() => 'both'),
      new Promise(resolve => setTimeout(() => resolve('waiting'), 100)),
    ])
    assert.equal(either, 'waiting')
    await waitForFile(path.join(current.controlDirectory, 'put-persisted'))
    const responseState = await Promise.race([
      Promise.all([first, second]).then(() => 2),
      new Promise(resolve => setTimeout(() => resolve(1), 50)),
    ])
    assert.equal(responseState, 1)
    await writeFile(path.join(current.controlDirectory, 'release'), '')
    const responses = await Promise.all([first, second])
    assert.deepEqual(responses.map(response => response.status), [200, 200])
    assert.equal(await responses[0].text(), 'stored')
    assert.equal(await responses[1].text(), 'stored')
  } finally {
    await current.close()
  }
})

test('failed and unarmed conditional PUTs pass through without claiming the arm', async () => {
  const current = await fixture(async (_incoming, response) => {
    response.writeHead(412, { 'content-type': 'text/plain' })
    response.end('PreconditionFailed')
  })
  try {
    await writeFile(path.join(current.controlDirectory, 'arm'), '')
    const failed = await request(current.gateUrl, { headers: { 'if-none-match': '*' } })
    assert.equal(failed.status, 412)
    assert.equal(await failed.text(), 'PreconditionFailed')
    assert.equal(await readFile(path.join(current.controlDirectory, 'arm'), 'utf8'), '')
    const unarmed = await request(current.gateUrl, { headers: { 'if-none-match': '"etag"' } })
    assert.equal(unarmed.status, 412)
    assert.equal(await unarmed.text(), 'PreconditionFailed')
    assert.equal(await readFile(path.join(current.controlDirectory, 'arm'), 'utf8'), '')
    assert.equal(await readFile(path.join(current.controlDirectory, 'put-persisted')).then(() => true, () => false), false)
  } finally {
    await current.close()
  }
})

test('successful conditional PUT without an arm passes through ungated', async () => {
  const current = await fixture(async (_incoming, response) => {
    response.writeHead(200)
    response.end('stored')
  })
  try {
    const response = await request(current.gateUrl, { headers: { 'if-none-match': '*' } })
    assert.equal(response.status, 200)
    assert.equal(await response.text(), 'stored')
    assert.equal(await readFile(path.join(current.controlDirectory, 'put-persisted')).then(() => true, () => false), false)
  } finally {
    await current.close()
  }
})

test('successful armed conditional PUT with an oversized upstream response passes through ungated', async () => {
  const largeBody = Buffer.alloc(1024 * 1024 + 1, 0x5a)
  const current = await fixture(async (_incoming, response) => {
    response.writeHead(200, { 'content-length': largeBody.length })
    response.end(largeBody)
  })
  try {
    await writeFile(path.join(current.controlDirectory, 'arm'), '')
    const response = await request(current.gateUrl, { headers: { 'if-none-match': '*' } })
    assert.equal(response.status, 200)
    assert.equal((await response.text()).length, largeBody.length)
    assert.equal(await readFile(path.join(current.controlDirectory, 'arm'), 'utf8'), '')
    assert.equal(await readFile(path.join(current.controlDirectory, 'put-persisted')).then(() => true, () => false), false)
  } finally {
    await current.close()
  }
})

test('upstream connection failure returns an error without claiming an armed request', async () => {
  const controlDirectory = await mkdtemp(path.join(os.tmpdir(), 'trykatch-storage-gate-'))
  const unusedServer = http.createServer()
  const unusedAddress = await listen(unusedServer)
  await new Promise(resolve => unusedServer.close(resolve))
  const gate = await startStorageGate({
    upstream: `http://127.0.0.1:${unusedAddress.port}`,
    controlDirectory,
    port: 0,
    host: '127.0.0.1',
  })
  try {
    await writeFile(path.join(controlDirectory, 'arm'), '')
    const response = await request(`http://127.0.0.1:${gate.address.port}`, { headers: { 'if-none-match': '*' } })
    assert.equal(response.status, 502)
    assert.equal(await response.text(), 'Bad Gateway')
    assert.equal(await readFile(path.join(controlDirectory, 'arm'), 'utf8'), '')
    assert.equal(await readFile(path.join(controlDirectory, 'put-persisted')).then(() => true, () => false), false)
  } finally {
    await gate.close()
    await rm(controlDirectory, { recursive: true, force: true })
  }
})

test('client disconnect releases a successful gate claim without fabricating a storage result', async () => {
  const current = await fixture(async (_incoming, response) => {
    response.writeHead(200)
    response.end('stored')
  })
  try {
    await writeFile(path.join(current.controlDirectory, 'arm'), '')
    const controller = new AbortController()
    const pending = request(current.gateUrl, { headers: { 'if-none-match': '*' }, signal: controller.signal })
    await waitForFile(path.join(current.controlDirectory, 'put-persisted'))
    controller.abort()
    await assert.rejects(pending)
    await new Promise(resolve => setTimeout(resolve, 50))
    assert.equal(await readFile(path.join(current.controlDirectory, 'release')).then(() => true, () => false), false)
    assert.equal(await readFile(path.join(current.controlDirectory, 'claimed')).then(() => true, () => false), false)
  } finally {
    await current.close()
  }
})
