import type { NavigationContribution } from '@trykatch/module-sdk'

export type WorkspaceNavigation = NavigationContribution & { anyPermissions?: readonly string[] }

// Both the sidebar and command palette consume this same deny-by-default list.
export function permittedWorkspaceNavigation(items: readonly WorkspaceNavigation[], permissions: readonly string[] = []) {
  const granted = new Set(permissions)
  return items.filter((item) => (!item.requiredPermission || granted.has(item.requiredPermission))
    && (!item.anyPermissions || item.anyPermissions.some((permission) => granted.has(permission))))
}
