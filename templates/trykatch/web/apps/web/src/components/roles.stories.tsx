import type { Meta, StoryObj } from '@storybook/react-vite'
import type { PermissionModuleDto } from '@trykatch/api-client'
import { expect, userEvent, within } from 'storybook/test'
import { RoleEditorDialog } from './RoleEditorDialog'
import { RolePermissionDisclosure } from './RolePermissionDisclosure'

const permissionModules: PermissionModuleDto[] = [{ key: 'projects', name: 'Projects', description: 'Organization project records', permissions: [
  { key: 'projects.read', name: 'Read projects', description: 'View project records', isSensitive: false, canGrant: true },
  { key: 'projects.manage', name: 'Manage projects', description: 'Change and archive projects', isSensitive: true, canGrant: true },
  { key: 'roles.manage', name: 'Manage roles', description: 'Change organization access', isSensitive: true, canGrant: false },
] }]
const meta = { title: 'Application UI/Role management', component: RoleEditorDialog, args: { open: true, modules: permissionModules, isLoading: false, isSaving: false, onOpenChange: () => undefined, onSave: () => undefined } } satisfies Meta<typeof RoleEditorDialog>
export default meta
type Story = StoryObj<typeof meta>
export const Create: Story = {}
export const Edit: Story = { args: { role: { name: 'Project operator', description: 'Manage delivery projects', permissions: ['projects.read'] } } }
export const Loading: Story = { args: { isLoading: true } }
export const Saving: Story = { args: { isSaving: true } }
export const Error: Story = { args: { error: 'Your changes could not be saved. Review and try again.' } }
export const Empty: Story = { args: { modules: [] } }
export const PermissionBoundary: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = within(body.getByRole('dialog'))
    await expect(dialog.getByRole('checkbox', { name: /Manage roles/ })).toBeDisabled()
    await userEvent.click(dialog.getByRole('button', { name: 'Select module' }))
    await expect(dialog.getByRole('checkbox', { name: /Read projects/ })).toBeChecked()
    await expect(dialog.getByRole('checkbox', { name: /Manage projects/ })).toBeChecked()
    await expect(dialog.getByRole('checkbox', { name: /Manage roles/ })).not.toBeChecked()
    await userEvent.click(dialog.getByRole('button', { name: 'Clear selection' }))
    await expect(dialog.getByRole('checkbox', { name: /Read projects/ })).not.toBeChecked()
  },
}
export const Disclosure: Story = { render: () => <RolePermissionDisclosure roleName="Project operator" permissionKeys={['projects.read', 'projects.manage']} modules={permissionModules} /> }
export const DisclosureLoading: Story = { render: () => <RolePermissionDisclosure roleName="Operator" permissionKeys={[]} modules={permissionModules} isLoading /> }
export const DisclosureEmpty: Story = { render: () => <RolePermissionDisclosure roleName="No access" permissionKeys={[]} modules={permissionModules} /> }
export const UnavailablePermission: Story = { render: () => <RolePermissionDisclosure roleName="Legacy role" permissionKeys={['retired.read']} modules={permissionModules} /> }
