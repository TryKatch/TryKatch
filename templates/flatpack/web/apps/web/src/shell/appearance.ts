export type Theme = 'light' | 'system' | 'dark' | 'custom'

export const defaultShellColor = '#0f766e'

export function readableForeground(hex: string) {
  const channels = [hex.slice(1, 3), hex.slice(3, 5), hex.slice(5, 7)].map((value) => Number.parseInt(value, 16) / 255)
  const [red, green, blue] = channels.map((value) => value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4)
  const luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue
  const blackContrast = (luminance + 0.05) / 0.05
  const whiteContrast = 1.05 / (luminance + 0.05)
  return blackContrast >= whiteContrast ? '#07110f' : '#ffffff'
}

export function applyAppearance(root: HTMLElement, theme: Theme, shellColor: string, prefersDark: boolean) {
  root.dataset.theme = theme === 'system' ? (prefersDark ? 'dark' : 'light') : theme
  root.style.setProperty('--theme-base-color', shellColor)
  root.style.setProperty('--theme-accent-foreground', readableForeground(shellColor))
  root.style.removeProperty('--accent')
  root.style.removeProperty('--accent-contrast')
}
