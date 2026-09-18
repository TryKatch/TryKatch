import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import axe from 'axe-core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { RoleEditorDialog, type PermissionModule } from './RoleEditorDialog'

const modules: PermissionModule[] = [
  {
    key: 'organization',
    name: 'Organization',
    description: 'Organization settings.',
    permissions: [{ key: 'organizations.read', name: 'View organization', description: 'View settings.', isSensitive: false, canGrant: true }],
  },
  {
    key: 'projects',
    name: 'Projects',
    description: 'Project records.',
    permissions: [
      { key: 'projects.read', name: 'View projects', description: 'View project details.', isSensitive: false, canGrant: true },
      { key: 'projects.manage', name: 'Manage projects', description: 'Change projects.', isSensitive: true, canGrant: true },
    ],
  },
]

describe('RoleEditorDialog', () => {
  afterEach(cleanup)
  it('collapses modules and lets people review only selected grants without losing others', () => {
    const onSave = vi.fn()
    render(<RoleEditorDialog open role={null} modules={modules} isLoading={false} isSaving={false} onOpenChange={vi.fn()} onSave={onSave} />)
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0)
    const projects = screen.getByRole('button', { name: 'Projects' })
    expect(projects).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(projects)
    fireEvent.click(screen.getByRole('checkbox', { name: /View projects/ }))
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'organization' } })
    fireEvent.click(screen.getByRole('checkbox', { name: /View organization/ }))
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: 'Selected only' }))
    expect(screen.getByRole('button', { name: 'Selected only' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getAllByRole('checkbox')).toHaveLength(2)
    expect(screen.queryByText('Manage projects')).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('textbox', { name: 'Role name' }), { target: { value: 'Reader' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save role' }))
    expect(onSave).toHaveBeenCalledWith({ name: 'Reader', description: '', permissions: ['organizations.read', 'projects.read'] })
  })

  it('opens matching modules but permits collapse while searching', () => {
    render(<RoleEditorDialog open modules={modules} isLoading={false} isSaving={false} onOpenChange={vi.fn()} onSave={vi.fn()} />)
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'projects.manage' } })
    const module = screen.getByRole('button', { name: 'Projects' })
    expect(module).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('checkbox', { name: /Manage projects/ })).toBeVisible()
    fireEvent.click(module)
    expect(module).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
  })

  it('does not bulk-grant disabled permissions and allows recovery from an empty selection filter', () => {
    const boundedModules = modules.map((module) => ({ ...module, permissions: module.permissions.map((permission) => ({ ...permission, canGrant: !permission.isSensitive })) }))
    render(<RoleEditorDialog open modules={boundedModules} isLoading={false} isSaving={false} onOpenChange={vi.fn()} onSave={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Selected only' }))
    expect(screen.getByText('No permissions selected yet.')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Show all permissions' }))
    const projectGroup = screen.getByRole('region', { name: 'Projects' })
    fireEvent.click(within(projectGroup).getByRole('button', { name: 'Select module' }))
    expect(within(projectGroup).getByRole('button', { name: 'Projects' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('checkbox', { name: /Manage projects/ })).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: /Manage projects/ })).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: /View projects/ })).toBeChecked()
  })

  it('blocks saving while the catalog is loading or a save is pending', () => {
    const onSave = vi.fn()
    const props = { open: true, role: { name: 'Reader', description: '', permissions: ['projects.read'] }, modules, onOpenChange: vi.fn(), onSave }
    const { rerender } = render(<RoleEditorDialog {...props} isLoading isSaving={false} />)
    expect(screen.getByRole('button', { name: 'Save role' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('textbox', { name: 'Role name' }).closest('form')!)
    expect(onSave).not.toHaveBeenCalled()
    rerender(<RoleEditorDialog {...props} isLoading={false} isSaving />)
    expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: /View projects/ })).toBeDisabled()
  })

  it('preserves draft edits on catalog refresh and resets them when the editor is reopened', () => {
    const role = { name: 'Reader', description: 'Project access', permissions: ['projects.read'] }
    const props = { open: true, role, modules, isLoading: false, isSaving: false, onOpenChange: vi.fn(), onSave: vi.fn() }
    const { rerender } = render(<RoleEditorDialog {...props} />)
    expect(screen.getByRole('button', { name: 'Projects' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('checkbox', { name: /View projects/ })).toBeChecked()
    fireEvent.change(screen.getByRole('textbox', { name: 'Role name' }), { target: { value: 'Draft reader' } })
    fireEvent.click(screen.getByRole('checkbox', { name: /Manage projects/ }))
    rerender(<RoleEditorDialog {...props} modules={[...modules]} error="Could not save role." />)
    expect(screen.getByRole('textbox', { name: 'Role name' })).toHaveValue('Draft reader')
    expect(screen.getByRole('checkbox', { name: /Manage projects/ })).toBeChecked()
    expect(screen.getByRole('alert')).toHaveTextContent('Could not save role.')
    rerender(<RoleEditorDialog {...props} open={false} />)
    rerender(<RoleEditorDialog {...props} />)
    expect(screen.getByRole('textbox', { name: 'Role name' })).toHaveValue('Reader')
    expect(screen.getByRole('checkbox', { name: /Manage projects/ })).not.toBeChecked()
  })

  it('explains invalid fields without saving and lets the user correct them', () => {
    const onSave = vi.fn()
    render(<RoleEditorDialog open modules={modules} isLoading={false} isSaving={false} onOpenChange={vi.fn()} onSave={onSave} />)
    const name = screen.getByRole('textbox', { name: 'Role name' })
    fireEvent.change(name, { target: { value: '   ' } })
    fireEvent.submit(name.closest('form')!)
    expect(screen.getByRole('alert')).toHaveTextContent('Role names must contain 1-80 characters.')
    expect(name).toHaveAttribute('aria-invalid', 'true')
    expect(onSave).not.toHaveBeenCalled()
    fireEvent.change(name, { target: { value: '  Project operator  ' } })
    fireEvent.submit(name.closest('form')!)
    expect(onSave).toHaveBeenCalledWith({ name: 'Project operator', description: '', permissions: [] })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('filters backend catalog metadata and selects a module accessibly', async () => {
    const onSave = vi.fn()
    render(<RoleEditorDialog open role={null} modules={modules} isLoading={false} isSaving={false} onOpenChange={vi.fn()} onSave={onSave} />)

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search permissions' }), { target: { value: 'project' } })
    expect(screen.queryByText('View organization')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Select module' }))
    expect(screen.getAllByRole('checkbox', { checked: true })).toHaveLength(2)
    expect(screen.getByText('1 sensitive permission selected')).toBeInTheDocument()
    expect(screen.queryByText('projects.manage')).not.toBeInTheDocument()

    fireEvent.change(screen.getByRole('textbox', { name: 'Role name' }), { target: { value: 'Project operator' } })
    fireEvent.change(screen.getByRole('textbox', { name: 'Purpose' }), { target: { value: 'Operates project records.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save role' }))
    expect(onSave).toHaveBeenCalledWith({ name: 'Project operator', description: 'Operates project records.', permissions: ['projects.manage', 'projects.read'] })

    const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
