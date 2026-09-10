import { defineWebModule } from '@trykatch/module-sdk'
import { FolderKanban } from 'lucide-react'
import { lazy } from 'react'

const ProjectsPage = lazy(() => import('./ProjectsPage').then((module) => ({ default: module.ProjectsPage })))

export const projectsModule = defineWebModule({
  id: 'projects',
  name: 'Projects',
  version: '1.0.0',
  description: 'Organization-scoped project management and lifecycle reference feature.',
  requires: [],
  optionalDependencies: [],
  routes: [
    { id: 'projects.list', path: '/projects', component: ProjectsPage },
  ],
  navigation: [
    { id: 'projects.navigation', section: 'Workspace', order: 20, to: '/projects', label: 'Projects', icon: FolderKanban },
  ],
  extensionPoints: [
    {
      id: 'projects.list.after-table',
      description: 'Renders module-owned workspace content after the projects table.',
      kind: 'ui-slot',
    },
  ],
  extensions: [],
})
