import type { PermissionModuleDto } from '@flatpackapp/api-client'
import { Badge, Button, Dialog } from '@flatpackapp/ui'
import { AlertTriangle, Check, Search, ShieldCheck } from 'lucide-react'
import { useEffect, useMemo, useState, type FormEvent } from 'react'

export type PermissionModule = PermissionModuleDto

export interface EditableRole {
  name: string
  description: string
  permissions: string[]
}

interface RoleEditorDialogProps {
  open: boolean
  role?: EditableRole | null
  modules: PermissionModule[]
  isLoading: boolean
  isSaving: boolean
  error?: string
  onOpenChange(open: boolean): void
  onSave(value: { name: string; description: string; permissions: string[] }): void
}

export function RoleEditorDialog({ open, role, modules, isLoading, isSaving, error, onOpenChange, onSave }: RoleEditorDialogProps) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())

  useEffect(() => {
    if (!open) return
    setName(role?.name ?? '')
    setDescription(role?.description ?? '')
    setQuery('')
    setSelected(new Set(role?.permissions ?? []))
  }, [open, role])

  const visibleModules = useMemo(() => {
    const normalized = query.trim().toLowerCase()
    if (!normalized) return modules
    return modules.map((module) => ({
      ...module,
      permissions: module.permissions.filter((permission) =>
        `${module.name} ${permission.name} ${permission.description} ${permission.key}`.toLowerCase().includes(normalized)),
    })).filter((module) => module.permissions.length > 0)
  }, [modules, query])

  const sensitiveCount = modules.flatMap((module) => module.permissions)
    .filter((permission) => permission.isSensitive && selected.has(permission.key)).length

  function togglePermission(key: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  function toggleModule(module: PermissionModule) {
    const grantable = module.permissions.filter((permission) => permission.canGrant).map((permission) => permission.key)
    const allSelected = grantable.length > 0 && grantable.every((key) => selected.has(key))
    setSelected((current) => {
      const next = new Set(current)
      grantable.forEach((key) => allSelected ? next.delete(key) : next.add(key))
      return next
    })
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    onSave({ name: name.trim(), description: description.trim(), permissions: [...selected].sort() })
  }

  return <Dialog
    open={open}
    onOpenChange={onOpenChange}
    title={role ? 'Edit custom role' : 'Create custom role'}
    description="Build a least-privilege role from the capabilities available to you."
    className="role-editor-dialog"
  >
    <form className="role-editor" onSubmit={submit}>
      <div className="role-identity-fields">
        <label className="role-name-field">Role name<input value={name} onChange={(event) => setName(event.target.value)} maxLength={80} placeholder="For example, Project operator" required autoFocus /></label>
        <label>Purpose<textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={240} rows={2} placeholder="Describe when this role should be assigned." /></label>
      </div>

      <section className="permission-editor" aria-labelledby="permission-editor-title">
        <header className="permission-editor-header">
          <div><h3 id="permission-editor-title"><ShieldCheck size={17} /> Permissions</h3><p>Grant only what people need for their responsibilities.</p></div>
          <div className="permission-summary"><strong>{selected.size}</strong><span>selected</span></div>
        </header>
        <div className="permission-toolbar">
          <label><Search size={15} /><span className="sr-only">Search permissions</span><input type="search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search permissions…" /></label>
          {selected.size > 0 && <Button type="button" variant="ghost" onClick={() => setSelected(new Set())}>Clear selection</Button>}
        </div>

        <div className="permission-groups" aria-live="polite">
          {isLoading ? <div className="permission-loading">Loading the permission catalog…</div> : visibleModules.length === 0 ? <div className="permission-empty">No permissions match “{query}”.</div> : visibleModules.map((module) => {
            const grantable = module.permissions.filter((permission) => permission.canGrant)
            const selectedInModule = module.permissions.filter((permission) => selected.has(permission.key)).length
            const allSelected = grantable.length > 0 && grantable.every((permission) => selected.has(permission.key))
            return <section className="permission-module" key={module.key} aria-labelledby={`permission-module-${module.key}`}>
              <header>
                <div><h4 id={`permission-module-${module.key}`}>{module.name}</h4><p>{module.description}</p></div>
                <div className="permission-module-actions"><span>{selectedInModule}/{module.permissions.length}</span><Button type="button" variant="ghost" disabled={grantable.length === 0} onClick={() => toggleModule(module)}>{allSelected ? 'Clear module' : 'Select module'}</Button></div>
              </header>
              <div className="permission-options">
                {module.permissions.map((permission) => <label className={`permission-option${selected.has(permission.key) ? ' is-selected' : ''}${!permission.canGrant ? ' is-disabled' : ''}`} key={permission.key}>
                  <input type="checkbox" checked={selected.has(permission.key)} disabled={!permission.canGrant} onChange={() => togglePermission(permission.key)} />
                  <span className="permission-check" aria-hidden="true">{selected.has(permission.key) && <Check size={13} />}</span>
                  <span className="permission-copy"><span><strong>{permission.name}</strong>{permission.isSensitive && <Badge tone="warning"><AlertTriangle size={10} /> Sensitive</Badge>}{!permission.canGrant && <Badge>Outside grant boundary</Badge>}</span><small>{permission.description}</small><code>{permission.key}</code></span>
                </label>)}
              </div>
            </section>
          })}
        </div>
      </section>

      {sensitiveCount > 0 && <div className="permission-warning"><AlertTriangle size={16} /><span><strong>{sensitiveCount} sensitive {sensitiveCount === 1 ? 'permission' : 'permissions'} selected</strong><small>Review these grants carefully before saving.</small></span></div>}
      {error && <div className="form-error" role="alert">{error}</div>}
      <footer className="role-editor-footer"><span>{selected.size === 0 ? 'This role will not grant access.' : `${selected.size} permission${selected.size === 1 ? '' : 's'} will be granted.`}</span><div><Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button><Button type="submit" variant="primary" disabled={isSaving || !name.trim()}>{isSaving ? 'Saving…' : 'Save role'}</Button></div></footer>
    </form>
  </Dialog>
}
