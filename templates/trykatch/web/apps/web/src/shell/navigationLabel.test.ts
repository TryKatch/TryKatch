import { describe, expect, it } from 'vitest'
import { navigationLabel } from './navigationLabel'

describe('shared sidebar and command-palette labels', () => {
  const item = { label: 'Shipment receptions', labels: { en: 'Shipment receptions', fr: 'Réceptions de livraisons' } }
  it.each(['fr', 'fr-CA'])('resolves module-owned French labels for %s', (locale) => {
    expect(navigationLabel(item, locale, () => 'host fallback')).toBe('Réceptions de livraisons')
  })
  it('resolves module-owned English labels', () => {
    expect(navigationLabel(item, 'en', () => 'host fallback')).toBe('Shipment receptions')
  })
  it('preserves host translations for legacy contributions', () => {
    expect(navigationLabel({ label: 'Projects' }, 'fr', () => 'Projets')).toBe('Projets')
  })
})
