import { access, rename, unlink, writeFile } from 'node:fs/promises'
import http from 'node:http'
import https from 'node:https'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const maxGatedResponseBytes = 1024 * 1024
const gateTimeoutMs = 120_000
const releasePollMs = 25

const exists = async file => access(file).then(() => true, () => false)

function writeChunk(response, chunk) {
  return new Promise(resolve => {
    if (response.destroyed || response.write(chunk)) {
      resolve()
      return
    }
    const finish = () => {
      response.removeListener('drain', finish)
      response.removeListener('close', finish)
      resolve()
    }
    response.once('drain', finish)
    response.once('close', finish)
  })
}

function writeUpstreamHeaders(upstreamResponse, downstreamResponse) {
  downstreamResponse.writeHead(upstreamResponse.statusCode ?? 502, upstreamResponse.statusMessage, upstreamResponse.headers)
}

function sendGatewayError(response) {
  if (response.destroyed || response.writableEnded) return
  if (response.headersSent) {
    response.destroy()
    return
  }
  response.writeHead(502, { 'content-type': 'text/plain; charset=utf-8', 'content-length': '11' })
  response.end('Bad Gateway')
}

async function bufferCandidateResponse(upstreamResponse, downstreamResponse) {
  const chunks = []
  let byteLength = 0
  let passthrough = false
  for await (const chunk of upstreamResponse) {
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
    if (passthrough) {
      await writeChunk(downstreamResponse, bytes)
      continue
    }
    chunks.push(bytes)
    byteLength += bytes.length
    if (byteLength > maxGatedResponseBytes) {
      passthrough = true
      writeUpstreamHeaders(upstreamResponse, downstreamResponse)
      for (const buffered of chunks) await writeChunk(downstreamResponse, buffered)
      chunks.length = 0
    }
  }
  if (passthrough) {
    if (!downstreamResponse.destroyed) downstreamResponse.end()
    return null
  }
  return Buffer.concat(chunks, byteLength)
}

async function claimArmedRequest(controlDirectory, gateState) {
  if (gateState.active) return false
  gateState.active = true
  const arm = path.join(controlDirectory, 'arm')
  const claimed = path.join(controlDirectory, 'claimed')
  try {
    await rename(arm, claimed)
    return true
  } catch (error) {
    gateState.active = false
    if (error?.code === 'ENOENT') return false
    throw error
  }
}

function waitForRelease(response, controlDirectory) {
  return new Promise(resolve => {
    let settled = false
    const release = path.join(controlDirectory, 'release')
    const finish = released => {
      if (settled) return
      settled = true
      clearTimeout(timeout)
      clearInterval(poll)
      response.removeListener('close', onClose)
      resolve(released)
    }
    const onClose = () => finish(false)
    const poll = setInterval(() => {
      void exists(release).then(found => { if (found) finish(true) })
    }, releasePollMs)
    const timeout = setTimeout(() => finish(false), gateTimeoutMs)
    response.once('close', onClose)
    if (response.destroyed) finish(false)
  })
}

async function proxyRequest(incoming, downstream, upstreamUrl, controlDirectory, gateState) {
  const conditional = incoming.method === 'PUT' && incoming.headers['if-none-match']?.trim() === '*'
  const shouldConsiderGate = conditional && await exists(path.join(controlDirectory, 'arm'))
  const transport = upstreamUrl.protocol === 'https:' ? https : http
  const upstreamRequest = transport.request({
    protocol: upstreamUrl.protocol,
    hostname: upstreamUrl.hostname,
    port: upstreamUrl.port || undefined,
    method: incoming.method,
    path: incoming.url || '/',
    setHost: false,
    headers: { ...incoming.headers, host: incoming.headers.host },
    auth: upstreamUrl.username || upstreamUrl.password
      ? `${decodeURIComponent(upstreamUrl.username)}:${decodeURIComponent(upstreamUrl.password)}`
      : undefined,
  })

  incoming.once('aborted', () => upstreamRequest.destroy())
  downstream.once('close', () => {
    if (!downstream.writableEnded) upstreamRequest.destroy()
  })

  upstreamRequest.once('error', () => sendGatewayError(downstream))
  upstreamRequest.once('response', upstreamResponse => {
    void (async () => {
      const gateCandidate = shouldConsiderGate && (upstreamResponse.statusCode ?? 500) >= 200 && (upstreamResponse.statusCode ?? 500) < 300
      if (!gateCandidate) {
        writeUpstreamHeaders(upstreamResponse, downstream)
        upstreamResponse.pipe(downstream)
        return
      }

      const body = await bufferCandidateResponse(upstreamResponse, downstream)
      if (body === null || downstream.destroyed) return
      const claimed = await claimArmedRequest(controlDirectory, gateState)
      if (!claimed) {
        writeUpstreamHeaders(upstreamResponse, downstream)
        downstream.end(body)
        return
      }

      try {
        await writeFile(path.join(controlDirectory, 'put-persisted'), 'persisted')
        const released = await waitForRelease(downstream, controlDirectory)
        if (!released || downstream.destroyed) {
          downstream.destroy()
          return
        }
        writeUpstreamHeaders(upstreamResponse, downstream)
        downstream.end(body)
      } finally {
        await unlink(path.join(controlDirectory, 'claimed')).catch(() => {})
        gateState.active = false
      }
    })().catch(() => {
      sendGatewayError(downstream)
      void upstreamResponse.destroy()
    })
  })
  incoming.pipe(upstreamRequest)
}

export async function startStorageGate({ upstream, controlDirectory, port = 9000, host = '0.0.0.0' } = {}) {
  if (!upstream || !controlDirectory) throw new TypeError('upstream and controlDirectory are required')
  const upstreamUrl = new URL(upstream)
  if (!['http:', 'https:'].includes(upstreamUrl.protocol)) throw new TypeError('upstream must use HTTP or HTTPS')
  const gateState = { active: false }
  const sockets = new Set()
  const server = http.createServer((incoming, downstream) => {
    void proxyRequest(incoming, downstream, upstreamUrl, controlDirectory, gateState)
      .catch(() => sendGatewayError(downstream))
  })
  server.on('connection', socket => {
    sockets.add(socket)
    socket.once('close', () => sockets.delete(socket))
  })
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(port, host, resolve)
  })
  return {
    address: server.address(),
    close: async () => {
      const closed = new Promise(resolve => server.close(resolve))
      for (const socket of sockets) socket.destroy()
      await closed
    },
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const upstream = process.env.QUOTA_STORAGE_UPSTREAM ?? 'http://object-storage:9000'
  const controlDirectory = process.env.QUOTA_STORAGE_CONTROL ?? '/control'
  const port = Number.parseInt(process.env.PORT ?? '9000', 10)
  startStorageGate({ upstream, controlDirectory, port }).then(({ address }) => {
    const boundPort = typeof address === 'object' && address ? address.port : port
    process.stdout.write(`quota storage gate listening on port ${boundPort}\n`)
  }).catch(error => {
    process.stderr.write(`quota storage gate failed to start: ${error.message}\n`)
    process.exitCode = 1
  })
}
