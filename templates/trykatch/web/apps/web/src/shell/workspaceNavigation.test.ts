import { describe, expect, it } from 'vitest'
import { permittedWorkspaceNavigation, type WorkspaceNavigation } from './workspaceNavigation'

const icon = () => null
const navigation: WorkspaceNavigation[] = [
  { id: 'overview', label: 'Overview', section: 'Workspace', order: 1, to: '/overview', icon },
  { id: 'people', label: 'User Management', section: 'Administration', order: 1, to: '/user-management', icon, anyPermissions: ['members.manage', 'roles.manage'] },
  { id: 'audit', label: 'Audit', section: 'Administration', order: 2, to: '/audit', icon, requiredPermission: 'audit.read' },
  { id: 'custom', label: 'Cooperatives', section: 'Workspace', order: 2, to: '/cooperatives', icon, requiredPermission: 'cooperatives.read' },
]

describe('effective-permission navigation', () => {
  it('does not expose administration to an ordinary member or while access is loading', () => {
    expect(permittedWorkspaceNavigation(navigation, ['members.read', 'roles.read', 'projects.manage']).map((item) => item.id)).toEqual(['overview'])
    expect(permittedWorkspaceNavigation(navigation).map((item) => item.id)).toEqual(['overview'])
  })
  it('adapts to assigned capabilities and module contributions without role-name branches', () => {
    expect(permittedWorkspaceNavigation(navigation, ['roles.manage', 'cooperatives.read']).map((item) => item.id)).toEqual(['overview', 'people', 'custom'])
    expect(permittedWorkspaceNavigation(navigation, ['members.manage', 'audit.read']).map((item) => item.id)).toEqual(['overview', 'people', 'audit'])
  })
})
