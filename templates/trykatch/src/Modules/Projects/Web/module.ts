import { defineWebModule } from '@trykatch/module-sdk'
import { getProjectsListQueryKey } from '@trykatch/api-client'
import { FolderKanban } from 'lucide-react'
import { lazy } from 'react'

const ProjectsPage = lazy(() => import('./ProjectsPage').then((module) => ({ default: module.ProjectsPage })))

export const projectsModule = defineWebModule({
  id: 'projects',
  name: 'Projects',
  version: '1.4.0',
  description: 'Organization-scoped project management and lifecycle reference feature.',
  requires: [],
  optionalDependencies: [],
  features: [
    { key: 'projects.reads', name: 'Project reads', description: 'View projects and their details.', defaultEnabled: true },
    { key: 'projects.writes', name: 'Project writes', description: 'Create, change, archive, restore, and request deletion of projects.', defaultEnabled: true },
  ],
  routes: [
    { id: 'projects.list', path: '/projects', component: ProjectsPage, requiredPermission: 'projects.read', requiredFeature: 'projects.reads' },
  ],
  navigation: [
    { id: 'projects.navigation', section: 'Workspace', order: 20, to: '/projects', label: 'Projects', icon: FolderKanban,
      requiredPermission: 'projects.read', requiredFeature: 'projects.reads' },
  ],
  extensionPoints: [
    {
      id: 'projects.list.after-table',
      description: 'Renders module-owned workspace content after the projects table.',
      kind: 'ui-slot',
    },
  ],
  extensions: [],
  realtimeInvalidations: [
    {
      id: 'projects.created',
      subscription: {
        policy: { ownerModuleId: 'projects', policyKey: 'projects.read', policyVersion: 1 },
        kind: 0,
      },
      matches: (notification) => notification.moduleId === 'projects'
        && notification.eventKey === 'projects.created' && notification.schemaVersion === 1,
      queryKeys: [getProjectsListQueryKey({ page: 1, pageSize: 100, lifecycle: 'active' }), ['projects']],
    },
  ],
})
