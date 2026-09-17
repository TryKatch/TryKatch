import type { PermissionModuleDto } from '@trykatch/api-client'
import { Badge, Button, Dialog } from '@trykatch/ui'
import { AlertTriangle, Check, ChevronDown, Search, ShieldCheck } from 'lucide-react'
import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { useI18n } from '../i18n/I18nProvider'

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
  const { t } = useI18n()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [selectedOnly, setSelectedOnly] = useState(false)
  const [expandedModules, setExpandedModules] = useState<Set<string>>(new Set())

  useEffect(() => {
    if (!open) return
    setName(role?.name ?? '')
    setDescription(role?.description ?? '')
    setQuery('')
    setSelected(new Set(role?.permissions ?? []))
    setSelectedOnly(false)
    setExpandedModules(new Set(modules.filter((module) => module.permissions.some((permission) => role?.permissions.includes(permission.key))).map((module) => module.key)))
  }, [open, role]) // Catalog refreshes must not discard edits or expansion state.

  const visibleModules = useMemo(() => {
    const normalized = query.trim().toLowerCase()
    return modules.map((module) => ({
      ...module,
      permissions: module.permissions.filter((permission) =>
        (!selectedOnly || selected.has(permission.key)) && `${module.name} ${permission.name} ${permission.description} ${permission.key}`.toLowerCase().includes(normalized)),
    })).filter((module) => module.permissions.length > 0)
  }, [modules, query, selectedOnly, selected])

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
    if (isLoading || isSaving || !name.trim()) return
    onSave({ name: name.trim(), description: description.trim(), permissions: [...selected].sort() })
  }

  return <Dialog
    open={open}
    onOpenChange={onOpenChange}
    title={t(role ? 'Edit custom role' : 'Create custom role')}
    description={t('Build a least-privilege role from the capabilities available to you.')}
    className="role-editor-dialog"
  >
    <form className="role-editor" onSubmit={submit}>
      <div className="role-identity-fields">
        <label className="role-name-field">{t('Role name')}<input value={name} onChange={(event) => setName(event.target.value)} maxLength={80} placeholder={t('For example, Project operator')} required autoFocus /></label>
        <label>{t('Purpose')}<textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={240} rows={2} placeholder={t('Describe when this role should be assigned.')} /></label>
      </div>

      <section className="permission-editor" aria-labelledby="permission-editor-title">
        <header className="permission-editor-header">
          <div><h3 id="permission-editor-title"><ShieldCheck size={17} /> {t('Permissions')}</h3><p>{t('Grant only what people need for their responsibilities.')}</p></div>
          <div className="permission-summary"><strong>{selected.size}</strong><span>{t('selected')}</span></div>
        </header>
        <div className="permission-toolbar">
          <label><Search size={15} /><span className="sr-only">{t('Search permissions')}</span><input type="search" value={query} onChange={(event) => { setQuery(event.target.value); if (event.target.value.trim()) setExpandedModules(new Set(modules.map((module) => module.key))) }} placeholder={t('Search permissions…')} /></label>
          <Button type="button" variant={selectedOnly ? 'secondary' : 'ghost'} aria-label={t('Selected only')} aria-pressed={selectedOnly} onClick={() => { setSelectedOnly((value) => !value); setExpandedModules(new Set(modules.map((module) => module.key))) }}>{t('Selected only')} <Badge>{selected.size}</Badge></Button>
          {selected.size > 0 && <Button type="button" variant="ghost" disabled={isSaving} onClick={() => setSelected(new Set())}>{t('Clear selection')}</Button>}
        </div>
        <p className="permission-catalog-hint">{t('Open a module to choose permissions. Search reveals matching permissions automatically.')}</p>

        <div className="permission-groups" aria-live="polite">
          {isLoading ? <div className="permission-loading" role="status">{t('Loading the permission catalog…')}</div> : visibleModules.length === 0 ? <div className="permission-empty"><span>{t(selectedOnly && !query.trim() ? 'No permissions selected yet.' : 'No permissions match')}{query.trim() ? ` “${query}”.` : ''}</span><Button type="button" variant="ghost" onClick={() => { setQuery(''); setSelectedOnly(false) }}>{t('Show all permissions')}</Button></div> : visibleModules.map((module) => {
            const grantable = module.permissions.filter((permission) => permission.canGrant)
            const selectedInModule = module.permissions.filter((permission) => selected.has(permission.key)).length
            const allSelected = grantable.length > 0 && grantable.every((permission) => selected.has(permission.key))
            const expanded = expandedModules.has(module.key)
            return <section className="permission-module" key={module.key} aria-labelledby={`permission-module-${module.key}`}>
              <header>
                <button type="button" className="permission-module-toggle" aria-label={module.name} aria-expanded={expanded} aria-controls={`permission-options-${module.key}`} onClick={() => setExpandedModules((current) => { const next = new Set(current); if (next.has(module.key)) next.delete(module.key); else next.add(module.key); return next })}>
                  <span className="permission-module-symbol"><ShieldCheck size={18} /></span><span><strong id={`permission-module-${module.key}`}>{module.name}</strong><small>{module.description}</small></span><ChevronDown size={16} />
                </button>
                <div className="permission-module-actions"><Badge tone={selectedInModule ? 'info' : 'neutral'}>{t('{selected} of {total} selected', { selected: selectedInModule, total: module.permissions.length })}</Badge><Button type="button" variant="ghost" disabled={isSaving || grantable.length === 0} onClick={() => toggleModule(module)}>{t(allSelected ? 'Clear module' : 'Select module')}</Button></div>
              </header>
              <div className="permission-options" id={`permission-options-${module.key}`} hidden={!expanded}>
                {module.permissions.map((permission) => <label className={`permission-option${selected.has(permission.key) ? ' is-selected' : ''}${!permission.canGrant ? ' is-disabled' : ''}`} key={permission.key}>
                  <input type="checkbox" checked={selected.has(permission.key)} disabled={isSaving || !permission.canGrant} onChange={() => togglePermission(permission.key)} />
                  <span className="permission-check" aria-hidden="true">{selected.has(permission.key) && <Check size={13} />}</span>
                  <span className="permission-copy"><span><strong>{permission.name}</strong>{permission.isSensitive && <Badge tone="warning"><AlertTriangle size={10} /> {t('Sensitive')}</Badge>}{!permission.canGrant && <Badge>{t('Outside grant boundary')}</Badge>}</span><small>{permission.description}</small><code>{permission.key}</code></span>
                </label>)}
              </div>
            </section>
          })}
        </div>
      </section>

      {sensitiveCount > 0 && <div className="permission-warning"><AlertTriangle size={16} /><span><strong>{sensitiveCount} {t('sensitive')} {t(sensitiveCount === 1 ? 'permission' : 'permissions')} {t('selected')}</strong><small>{t('Review these grants carefully before saving.')}</small></span></div>}
      {error && <div className="form-error" role="alert">{error}</div>}
      <footer className="role-editor-footer"><span>{selected.size === 0 ? t('This role will not grant access.') : `${selected.size} ${t(selected.size === 1 ? 'permission' : 'permissions')} ${t('will be granted.')}`}</span><div><Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={isLoading || isSaving || !name.trim()}>{t(isSaving ? 'Saving…' : 'Save role')}</Button></div></footer>
    </form>
  </Dialog>
}
