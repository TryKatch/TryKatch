import { defineConfig } from 'vitest/config'
import { playwright } from '@vitest/browser-playwright'
import { storybookTest } from '@storybook/addon-vitest/vitest-plugin'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  plugins: [storybookTest({ configDir: fileURLToPath(new URL('./.storybook', import.meta.url)) })],
  test: {
    name: 'storybook',
    browser: { enabled: true, headless: true, provider: playwright(), instances: [{ browser: 'chromium' }] },
  },
})
