import type { StorybookConfig } from '@storybook/react-vite'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath } from 'node:url'

const config: StorybookConfig = {
  stories: [
    '../../../packages/*/src/**/*.stories.@(ts|tsx)',
    '../../web/src/**/*.stories.@(ts|tsx)',
    '../../../../src/Modules/*/Web/*.stories.@(ts|tsx)',
    '../../../../src/Modules/*/Web/src/**/*.stories.@(ts|tsx)',
  ],
  framework: '@storybook/react-vite',
  core: { disableTelemetry: true },
  addons: ['@storybook/addon-docs', '@storybook/addon-a11y', '@storybook/addon-vitest'],
  staticDirs: ['../public'],
  async viteFinal(config) {
    // Do not inherit the web application's API proxy or production entrypoint.
    return { ...config, plugins: [...(config.plugins ?? []), tailwindcss()], server: { ...config.server, proxy: {}, fs: { ...config.server?.fs, allow: [fileURLToPath(new URL('../../../../', import.meta.url))] } } }
  },
}
export default config
