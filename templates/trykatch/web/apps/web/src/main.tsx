import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from '@tanstack/react-router'
import { router } from './router'
import { I18nProvider } from './i18n/I18nProvider'
import { applyAppearance, defaultShellColor, type Theme } from './shell/appearance'
import './styles.css'

const storedTheme = localStorage.getItem('trykatch-theme')
const theme: Theme = storedTheme === 'light' || storedTheme === 'dark' || storedTheme === 'custom' || storedTheme === 'system' ? storedTheme : 'system'
const storedShellColor = localStorage.getItem('trykatch-shell-color')
const shellColor = storedShellColor && /^#[0-9a-f]{6}$/i.test(storedShellColor) ? storedShellColor : defaultShellColor
applyAppearance(document.documentElement, theme, shellColor, matchMedia('(prefers-color-scheme: dark)').matches)

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, retry: 1 } },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <I18nProvider>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </I18nProvider>
  </StrictMode>,
)
