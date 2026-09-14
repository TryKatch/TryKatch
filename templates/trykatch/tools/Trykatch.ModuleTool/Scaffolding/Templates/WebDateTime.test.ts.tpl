import { describe, expect, it } from 'vitest'
import { toDateTimeLocal, toUtcDateTime } from './dateTime'

describe('generated date-time transport', () => {
  it('preserves the exact original instant when an unrelated field is edited', () => {
    const original = '2026-09-14T10:20:45.1234567+02:00'
    expect(toUtcDateTime(toDateTimeLocal(original), original)).toBe(original)
  })

  it('converts an intentionally changed local value to an ISO UTC instant', () => {
    const changed = '2026-09-14T10:20:45.123'
    expect(toUtcDateTime(changed)).toBe(new Date(changed).toISOString())
  })
})
