import { describe, expect, it } from 'vitest'
import { applyAppearance, readableForeground } from './appearance'

describe('shell appearance', () => {
  it('treats Custom as a complete fourth theme and never overrides only the accent', () => {
    const root = document.createElement('html')

    applyAppearance(root, 'custom', '#6b16ed', false)

    expect(root.dataset.theme).toBe('custom')
    expect(root.style.getPropertyValue('--theme-base-color')).toBe('#6b16ed')
    expect(root.style.getPropertyValue('--theme-accent-foreground')).toBe('#ffffff')
    expect(root.style.getPropertyValue('--accent')).toBe('')
  })

  it('resolves System independently from the saved custom color', () => {
    const root = document.createElement('html')
    applyAppearance(root, 'system', '#facc15', true)
    expect(root.dataset.theme).toBe('dark')
  })

  it('chooses a readable foreground for bright and dark shell colors', () => {
    expect(readableForeground('#facc15')).toBe('#07110f')
    expect(readableForeground('#221144')).toBe('#ffffff')
  })
})
