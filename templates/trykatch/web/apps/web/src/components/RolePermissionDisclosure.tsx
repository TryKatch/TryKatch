import type { PermissionModuleDto } from '@trykatchapp/api-client'
import { Badge } from '@trykatchapp/ui'
import { AlertTriangle, ChevronRight, ShieldCheck } from 'lucide-react'
import { useMemo } from 'react'

interface RolePermissionDisclosureProps {
  roleName: string
  permissionKeys: readonly string[]
  modules: readonly PermissionModuleDto[]
  isLoading?: boolean
}

export function RolePermissionDisclosure({ roleName, permissionKeys, modules, isLoading = false }: RolePermissionDisclosureProps) {
  const { grantedModules, unknownKeys } = useMemo(() => {
    const granted = new Set(permissionKeys)
    const known = new Set<string>()
    const matchingModules = modules.map((module) => {
      const permissions = module.permissions.filter((permission) => {
        known.add(permission.key)
        return granted.has(permission.key)
      })
      return { ...module, permissions }
    }).filter((module) => module.permissions.length > 0)

    return {
      grantedModules: matchingModules,
      unknownKeys: permissionKeys.filter((key) => !known.has(key)),
    }
  }, [modules, permissionKeys])

  if (isLoading) return <div className="role-permission-loading" role="status">Loading permission details…</div>

  return <section className="role-permission-disclosure" aria-label={`Permissions for ${roleName}`}>
    <header className="role-permission-overview">
      <div><span className="eyebrow">Access included</span><strong>{permissionKeys.length === 0 ? 'No permissions granted' : `${permissionKeys.length} permission${permissionKeys.length === 1 ? '' : 's'} across ${grantedModules.length + (unknownKeys.length ? 1 : 0)} ${grantedModules.length + (unknownKeys.length ? 1 : 0) === 1 ? 'module' : 'modules'}`}</strong></div>
      <p>Open a module to inspect its grants. Only one role stays expanded at a time.</p>
    </header>

    {permissionKeys.length === 0 ? <div className="role-permission-empty"><ShieldCheck size={18} /><span>This role does not grant organization access.</span></div> : <div className="role-permission-groups">
      {grantedModules.map((module) => <details className="role-permission-module" key={module.key}>
        <summary>
          <span className="role-permission-module-title"><ShieldCheck size={16} /><span><strong>{module.name}</strong><small>{module.description}</small></span></span>
          <span className="role-permission-module-meta"><Badge>{module.permissions.length} {module.permissions.length === 1 ? 'grant' : 'grants'}</Badge><ChevronRight size={16} /></span>
        </summary>
        <ul>
          {module.permissions.map((permission) => <li key={permission.key}>
            <span><strong>{permission.name}</strong>{permission.isSensitive && <Badge tone="warning"><AlertTriangle size={10} /> Sensitive</Badge>}</span>
            <small>{permission.description}</small>
            <code>{permission.key}</code>
          </li>)}
        </ul>
      </details>)}
      {unknownKeys.length > 0 && <details className="role-permission-module role-permission-unknown">
        <summary>
          <span className="role-permission-module-title"><AlertTriangle size={16} /><span><strong>Unavailable definitions</strong><small>Stored grants that are no longer present in the active catalog.</small></span></span>
          <span className="role-permission-module-meta"><Badge tone="warning">{unknownKeys.length}</Badge><ChevronRight size={16} /></span>
        </summary>
        <ul>{unknownKeys.map((key) => <li key={key}><strong>Retired or unavailable permission</strong><code>{key}</code></li>)}</ul>
      </details>}
    </div>}
  </section>
}
