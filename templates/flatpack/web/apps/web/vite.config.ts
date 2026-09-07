import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: process.env.services__api__https__0 ?? 'https://localhost:7240', secure: false },
      '/connect': { target: process.env.services__api__https__0 ?? 'https://localhost:7240', secure: false },
    },
  },
})
