import { describe, expect, it } from 'vitest'
import contract from '../openapi/Trykatch.Api.json'

describe('OpenAPI example privacy', () => {
  it('never logs response bodies that can contain credentials or personal data', () => {
    const samples = Object.values(contract.paths).flatMap((path) => Object.values(path)).flatMap((operation) =>
      'x-codeSamples' in operation ? operation['x-codeSamples'] : [])
    expect(samples.length).toBeGreaterThan(0)
    for (const sample of samples) {
      expect(sample.source).not.toMatch(/console\.[a-z]+\(result\)/)
      expect(sample.source).toContain('console.log({ status: response.status });')
    }
  })
})
