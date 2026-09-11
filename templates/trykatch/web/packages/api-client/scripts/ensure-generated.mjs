import { existsSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const packageDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const requiredOutputs = [
  'src/generated/client.ts',
  'src/generated/models/index.ts',
]

const missingOutputs = () =>
  requiredOutputs.filter((file) => !existsSync(path.join(packageDirectory, file)))

const missing = missingOutputs()
if (missing.length === 0) {
  process.exit(0)
}

console.log(`Generated API client is incomplete (${missing.join(', ')}). Regenerating it...`)

const pnpm = process.platform === 'win32' ? 'pnpm.cmd' : 'pnpm'
const generation = spawnSync(pnpm, ['run', 'generate'], {
  cwd: packageDirectory,
  encoding: 'utf8',
  stdio: 'inherit',
})

if (generation.error) {
  console.error(`Could not start API client generation: ${generation.error.message}`)
  process.exit(1)
}

if (generation.status !== 0) {
  process.exit(generation.status ?? 1)
}

const stillMissing = missingOutputs()
if (stillMissing.length > 0) {
  console.error(`API client generation completed without producing: ${stillMissing.join(', ')}`)
  process.exit(1)
}
