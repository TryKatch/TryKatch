import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'

const production = fileURLToPath(new URL('../web/apps/web/dist/', import.meta.url))
function inspect(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    assert.doesNotMatch(entry.name, /mockServiceWorker|\.stories[.-]|storybook/i, `Development asset in production: ${path}`)
    if (entry.isDirectory()) inspect(path)
    else if (/\.(js|html)$/.test(entry.name)) {
      assert.doesNotMatch(readFileSync(path, 'utf8'), /storybook-only|preview@example\.test|@storybook\//, `Storybook fixture/tooling in production: ${path}`)
    }
  }
}
inspect(production)
console.log('Production web output excludes Storybook assets and fixture sentinels.')
