import { defineConfig, devices } from '@playwright/test'

const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL

export default defineConfig({
  testDir: './e2e',
  outputDir: process.env.TRYKATCH_PLAYWRIGHT_OUTPUT_DIR ?? 'test-results',
  use: {
    baseURL: acceptanceBaseUrl ?? 'http://127.0.0.1:4173',
    ignoreHTTPSErrors: acceptanceBaseUrl?.startsWith('https://') ?? false,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: acceptanceBaseUrl
    ? undefined
    : { command: 'pnpm build && pnpm exec vite preview --host 127.0.0.1 --port 4173', port: 4173, reuseExistingServer: !process.env.CI },
})
