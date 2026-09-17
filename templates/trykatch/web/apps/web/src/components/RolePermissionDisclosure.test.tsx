import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { RolePermissionDisclosure } from './RolePermissionDisclosure'

afterEach(cleanup)

it('shows friendly access labels without exposing technical permission identifiers', () => {
  render(<RolePermissionDisclosure roleName="Member" permissionKeys={['projects.manage']} modules={[{
    key: 'projects', name: 'Projects', description: 'Workspace projects',
    permissions: [{ key: 'projects.manage', name: 'Manage projects', description: 'Create and update projects.', isSensitive: true, canGrant: true }],
  }]} />)
  expect(screen.getByText('Manage projects')).toBeInTheDocument()
  expect(screen.queryByText('projects.manage')).not.toBeInTheDocument()
})
