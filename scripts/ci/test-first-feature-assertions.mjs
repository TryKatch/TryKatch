import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const harness = readFileSync(new URL('../test-first-feature.sh', import.meta.url), 'utf8')
const assertions = harness.split('\n').filter(line => /^\s*(?:rg|grep)\s+-/.test(line))
const fixture = fileURLToPath(new URL('./fixtures/first-feature-assertions/', import.meta.url))

test('actual first-feature log assertions work without optional search tools', () => {
  assert.equal(assertions.length, 6, 'Exercise all six actual harness assertions')
  const script = 'feature_test_root=$1\n' + assertions.join('\n')
  const result = spawnSync('/bin/bash', ['-euc', script, 'assertions', fixture], {
    encoding: 'utf8', env: { PATH: '/usr/bin:/bin' },
  })
  assert.equal(result.status, 0, result.error?.message ?? result.stderr)
})

test('actual first-feature log assertions reject missing generation evidence', () => {
  const script = 'feature_test_root=$1\n' + assertions.join('\n')
  const result = spawnSync('/bin/bash', ['-euc', script, 'assertions', fixture + 'incomplete'], {
    encoding: 'utf8', env: { PATH: '/usr/bin:/bin' },
  })
  assert.equal(result.status, 1, 'Missing architecture progress must fail, not silently pass')
})
