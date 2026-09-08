import { customFetch } from '@flatpackapp/api-client'
import { defineFlatpackWebModule, type FlatpackWebExtensionProps } from '@flatpackapp/module-sdk'
import { EmptyState, PageHeader, Skeleton, Surface } from '@flatpackapp/ui'
import { Blocks, Check, CircleDashed } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import './styles.css'

export interface GettingStartedStep {
  id: string
  title: string
  description: string
  complete: boolean
}

export interface GettingStartedResponse {
  moduleId: string
  organization: string
  steps: GettingStartedStep[]
}

function useGettingStarted() {
  return useQuery({
    queryKey: ['module', 'getting-started'],
    queryFn: () => customFetch<GettingStartedResponse>('/api/v1/getting-started', { method: 'GET' }),
  })
}

export function GettingStartedPage() {
  const query = useGettingStarted()
  return <>
    <PageHeader
      eyebrow="Reference module"
      title="Getting started"
      description="A live vertical slice installed through Flatpack's backend and frontend module seams."
    />
    <Surface className="flatpack-module-surface">
      {query.isPending ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div>
        : query.isError ? <EmptyState title="Module could not be loaded" description={query.error.message} />
          : <div className="flatpack-module-checklist">
            <header><Blocks size={19} /><div><strong>Module integration</strong><span>Workspace: {query.data.organization}</span></div></header>
            <ul>{query.data.steps.map((step) => <li key={step.id}>
              <span className={step.complete ? 'is-complete' : undefined}>{step.complete ? <Check size={14} /> : <CircleDashed size={14} />}</span>
              <div><strong>{step.title}</strong><small>{step.description}</small></div>
            </li>)}</ul>
          </div>}
    </Surface>
  </>
}

export function ProjectsIntegration({ context }: FlatpackWebExtensionProps) {
  const query = useGettingStarted()
  if (!query.data) return null
  const count = typeof context.resultCount === 'number' ? context.resultCount : 0
  return <aside className="flatpack-module-extension" aria-label="Getting Started module integration">
    <span><Check size={13} /></span>
    <div><strong>Getting Started module active</strong><small>Extended this screen without changing Projects · {count} active {count === 1 ? 'project' : 'projects'}</small></div>
  </aside>
}

export const gettingStartedModule = defineFlatpackWebModule({
  id: 'getting-started',
  name: 'Getting Started',
  version: '1.0.0',
  description: "A reference module that proves Flatpack's backend and frontend composition seams.",
  requires: ['projects'],
  optionalDependencies: [],
  routes: [
    { id: 'getting-started.overview', path: '/getting-started', component: GettingStartedPage },
  ],
  navigation: [
    { id: 'getting-started.navigation', section: 'Workspace', order: 90, to: '/getting-started', label: 'Getting Started', icon: Blocks, requiredPermission: 'getting-started.read' },
  ],
  extensionPoints: [],
  extensions: [
    { id: 'getting-started.projects-status', point: 'projects.list.after-table', order: 10, requiredPermission: 'getting-started.read', component: ProjectsIntegration },
  ],
})
