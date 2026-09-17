import { fireEvent, render, screen } from '@testing-library/react'
import axe from 'axe-core'
import { describe, expect, it, vi } from 'vitest'
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
