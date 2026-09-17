import type { Preview } from '@storybook/react-vite'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from '@tanstack/react-router'
import { useEffect, useState, type ComponentType } from 'react'
import { http, HttpResponse } from 'msw'
import { setupWorker } from 'msw/browser'
import { mswLoader } from 'msw-storybook-addon/csf3'
import { ModuleProvider } from '@trykatch/module-sdk'
import { workspaceModules } from '../../web/src/modules'
import { I18nProvider } from '../../web/src/i18n/I18nProvider'
import { AccountSecurityCompletionBoundary } from '../../web/src/components/AccountSecurityCompletion'
import '../../web/src/styles.css'

const unexpectedRequests: string[] = []

function StoryProviders({ story: Story }: { story: ComponentType }) {
  const [query] = useState(() => new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } }))
  const [router] = useState(() => {
    const root = createRootRoute({ component: Outlet })
    const index = createRoute({ getParentRoute: () => root, path: '/', component: Story })
    const page = createRoute({ getParentRoute: () => root, path: '$', component: Story })
    return createRouter({ routeTree: root.addChildren([index, page]), history: createMemoryHistory({ initialEntries: ['/'] }) })
  })
  useEffect(() => () => { void query.cancelQueries(); query.clear() }, [query])
  return <QueryClientProvider client={query}><I18nProvider><AccountSecurityCompletionBoundary><ModuleProvider catalog={workspaceModules}><RouterProvider router={router} /></ModuleProvider></AccountSecurityCompletionBoundary></I18nProvider></QueryClientProvider>
}

const preview: Preview = {
  tags: ['autodocs'],
  globalTypes: {
    theme: { description: 'Application theme', toolbar: { icon: 'circlehollow', items: ['light', 'dark'], dynamicTitle: true } },
    locale: { description: 'Application language', toolbar: { icon: 'globe', items: [{ value: 'en', title: 'English' }, { value: 'fr', title: 'Français' }], dynamicTitle: true } },
  },
  initialGlobals: { theme: 'light', locale: 'en' },
  parameters: {
    layout: 'padded',
    a11y: { test: 'error' },
    viewport: { options: {
      phone: { name: 'Phone 320px', styles: { width: '320px', height: '800px' } },
      tablet: { name: 'Tablet 768px', styles: { width: '768px', height: '1024px' } },
      desktop: { name: 'Desktop 1440px', styles: { width: '1440px', height: '900px' } },
    } },
    msw: { handlers: { auth: [
      http.get('*/api/v1/auth/session', () => HttpResponse.json({ userId: '00000000-0000-0000-0000-000000000001', email: 'preview@example.test', displayName: 'Preview user', isPlatformAdministrator: false, hasPlatformAccess: false, platformRole: null, platformPermissions: [] })),
      http.get('*/api/v1/auth/antiforgery', () => HttpResponse.json({ token: 'storybook-only' })),
      http.get('*/api/v1/access', () => HttpResponse.json({ permissions: [] })),
    ] } },
  },
  loaders: [mswLoader(async () => {
    const worker = setupWorker()
    await worker.start({ quiet: true, onUnhandledRequest(request, print) {
      if (/^\/(api|connect)(\/|$)/.test(new URL(request.url).pathname)) {
        unexpectedRequests.push(`${request.method} ${request.url}`)
        print.error()
      }
    } })
    return worker
  })],
  beforeEach(context) {
    unexpectedRequests.length = 0
    const previousLocale = localStorage.getItem('trykatch-locale')
    const previousTheme = document.documentElement.dataset.theme
    localStorage.setItem('trykatch-locale', context.globals.locale)
    document.documentElement.dataset.theme = context.globals.theme
    return () => {
      if (previousLocale === null) localStorage.removeItem('trykatch-locale')
      else localStorage.setItem('trykatch-locale', previousLocale)
      if (previousTheme === undefined) delete document.documentElement.dataset.theme
      else document.documentElement.dataset.theme = previousTheme
    }
  },
  afterEach() {
    if (unexpectedRequests.length) throw new Error(`Story has unmocked API requests:\n${unexpectedRequests.join('\n')}`)
  },
  decorators: [(Story, context) => <StoryProviders key={`${context.id}:${context.globals.locale}:${context.globals.theme}`} story={Story} />],
}
export default preview
