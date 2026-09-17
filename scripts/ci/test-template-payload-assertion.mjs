import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'

const harness = readFileSync(new URL('../test-template.sh', import.meta.url), 'utf8')
const assertion = harness.match(/grep -Fq "body: JSON.stringify[^\n]+\n[^\n]+\n[^\n]+/)[0]
const payload = "body: JSON.stringify({ sku, price, discount: discount || null, sequence, available, availableAt: availableAt === '' ? null : toUtcDateTime(availableAt, editing?.availableAt), category, notes: notes || null, ...(editing ? { expectedVersion: editing.version } : {}) }),"

function check(source) {
  const root = mkdtempSync(join(tmpdir(), 'trykatch-payload-assertion-'))
  try {
    const web = join(root, 'Horizon/src/Modules/Inventory/Web/src')
    mkdirSync(web, { recursive: true })
    writeFileSync(join(web, 'index.tsx'), source)
    return spawnSync('/bin/bash', ['--noprofile', '--norc', '-euc',
      'test_root=$1\nfail() { exit 1; }\n' + assertion, 'check', root], {
      encoding: 'utf8', env: { PATH: '/usr/bin:/bin' },
    }).status
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

test('actual packaging assertion accepts business fields with version-protected updates', () => {
  assert.equal(check(payload), 0)
})

test('actual packaging assertion rejects missing business fields', () => {
  assert.equal(check(payload.replace('sku, price,', 'price,')), 1)
})

test('actual packaging assertion rejects missing concurrency protection', () => {
  assert.equal(check(payload.replace(', ...(editing ? { expectedVersion: editing.version } : {})', '')), 1)
})
