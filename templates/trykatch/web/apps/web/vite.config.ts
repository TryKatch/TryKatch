import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

const hasLaunchVideo = existsSync(fileURLToPath(new URL('./public/marketing/trykatch-launch.mp4', import.meta.url)))

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    // Marketing media is deliberately absent from generated products.
    'import.meta.env.VITE_TRYKATCH_LAUNCH_VIDEO': JSON.stringify(
      hasLaunchVideo && process.env.VITE_TRYKATCH_LAUNCH_VIDEO !== 'false' ? 'true' : 'false',
    ),
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: process.env.services__api__https__0 ?? 'https://localhost:7240', secure: false },
      '/connect': { target: process.env.services__api__https__0 ?? 'https://localhost:7240', secure: false },
    },
  },
})
