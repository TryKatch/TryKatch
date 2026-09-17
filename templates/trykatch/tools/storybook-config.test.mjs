import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const root = fileURLToPath(new URL('../web/', import.meta.url))
const json = path => JSON.parse(readFileSync(root + path, 'utf8'))

test('developers can start, build and test the one workspace catalogue', () => {
  const scripts = json('package.json').scripts
  assert.equal(scripts.storybook, 'pnpm --filter @trykatch/storybook storybook')
  assert.equal(scripts['storybook:build'], 'pnpm --filter @trykatch/storybook storybook:build')
  assert.equal(scripts['storybook:test'], 'pnpm --filter @trykatch/storybook storybook:test')
  const catalogue = json('apps/storybook/package.json')
  assert.ok(catalogue.scripts['storybook:build'].includes('storybook build'))
  assert.ok(catalogue.scripts['storybook:test'].includes('vitest run'))
})
