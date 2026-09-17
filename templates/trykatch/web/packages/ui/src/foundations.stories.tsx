import type { Meta, StoryObj } from '@storybook/react-vite'
import { Button, Surface } from './primitives'

export default { title: 'Foundations/Brand tokens', parameters: { docs: { description: { component: 'Live application CSS variables, not a separate Storybook palette. Use the theme toolbar to compare light and dark. Focus styles are shown by tabbing through the controls.' } } } } satisfies Meta
type Story = StoryObj
export const Colours: Story = {
  render: () => <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: 16 }}>{['bg', 'panel', 'panel-raised', 'text', 'muted', 'line', 'line-strong', 'accent', 'accent-hover', 'accent-soft', 'accent-ink', 'accent-contrast', 'danger', 'focus'].map((token) => <Surface key={token} style={{ padding: 12 }}><div style={{ height: 64, background: `var(--${token})`, border: '1px solid var(--line)', borderRadius: 'var(--radius)' }} /><code>--{token}</code></Surface>)}</div>,
}
export const TypographySpacingAndBorders: Story = {
  render: () => <Surface style={{ padding: 24 }}><h1>Application heading</h1><h2>Section heading</h2><h3>Panel heading</h3><p>Body text uses the real application font stack and line height.</p><small>Secondary information</small><p><code>Code and identifiers</code></p><div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>{[4, 8, 12, 16, 24, 32].map((space) => <div key={space} style={{ padding: space, border: '1px solid var(--line-strong)', borderRadius: 'var(--radius)' }}>{space}px</div>)}</div><p><Button>Keyboard focus example</Button></p></Surface>,
}
