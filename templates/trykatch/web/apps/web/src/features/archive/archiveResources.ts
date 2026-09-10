import { customFetch, type RoleDto } from '@trykatch/api-client'
import type { ArchiveResourceContribution } from '@trykatch/module-sdk'
import type { RecordLifecycle } from '../../components/RecordLifecycle'
import { workspaceModules } from '../../modules'

export type ArchiveResourceKind = string

export interface ArchiveItem {
  id: string
  kind: ArchiveResourceKind
  typeLabel: string
  title: string
  description: string
  lifecycle: RecordLifecycle
}

interface ProjectRecord { id: string; name: string; description: string; lifecycle: RecordLifecycle }
interface ProjectPage { items: ProjectRecord[]; page: number; pageSize: number; totalCount: number }
interface MemberRecord { id: string; displayName: string; email: string; roles: Array<{ name: string }>; lifecycle: RecordLifecycle }
interface InvitationRecord { id: string; email: string; status: string; expiresAt: string; lifecycle: RecordLifecycle }

export interface ArchiveResourceDefinition {
  kind: ArchiveResourceKind
  typeLabel: string
  readPermission: string
  managePermission: string
  load(): Promise<ArchiveItem[]>
  restore(id: string): Promise<unknown>
  requestDeletion?(id: string, reason: string): Promise<unknown>
}

function encode(value: string) {
  return encodeURIComponent(value)
}

function asArchiveItem(definition: Pick<ArchiveResourceDefinition, 'kind' | 'typeLabel'>, record: { id: string; lifecycle: RecordLifecycle }, title: string, description: string): ArchiveItem {
  return { id: record.id, kind: definition.kind, typeLabel: definition.typeLabel, title, description, lifecycle: record.lifecycle }
}

async function loadAllProjectPages() {
  const path = '/api/v1/projects?lifecycle=recoverable&pageSize=100'
  const first = await customFetch<ProjectPage>(`${path}&page=1`, { method: 'GET' })
  const remainingPageCount = Math.max(0, Math.ceil(Number(first.totalCount) / first.pageSize) - 1)
  const remaining = await Promise.all(Array.from({ length: remainingPageCount }, (_, index) =>
    customFetch<ProjectPage>(`${path}&page=${index + 2}`, { method: 'GET' })))
  return [first, ...remaining].flatMap((page) => page.items)
}

export const archiveResourceDefinitions: readonly ArchiveResourceDefinition[] = [
  {
    kind: 'project',
    typeLabel: 'Project',
    readPermission: 'projects.read',
    managePermission: 'projects.manage',
    async load() {
      return (await loadAllProjectPages()).map((record) =>
        asArchiveItem({ kind: 'project', typeLabel: 'Project' }, record, record.name, record.description || 'Organization project'))
    },
    restore: (id) => customFetch<void>(`/api/v1/projects/${encode(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id, reason) => customFetch<void>(`/api/v1/projects/${encode(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  },
  {
    kind: 'member',
    typeLabel: 'Person',
    readPermission: 'members.read',
    managePermission: 'members.manage',
    async load() {
      const records = await customFetch<MemberRecord[]>('/api/v1/members?lifecycle=recoverable', { method: 'GET' })
      return records.map((record) => asArchiveItem({ kind: 'member', typeLabel: 'Person' }, record, record.displayName || record.email, `${record.email} · ${record.roles.map((role) => role.name).join(', ') || 'No roles'}`))
    },
    restore: (id) => customFetch<void>(`/api/v1/members/${encode(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id, reason) => customFetch<void>(`/api/v1/members/${encode(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  },
  {
    kind: 'invitation',
    typeLabel: 'Invitation',
    readPermission: 'members.read',
    managePermission: 'members.manage',
    async load() {
      const records = await customFetch<InvitationRecord[]>('/api/v1/invitations?lifecycle=recoverable', { method: 'GET' })
      return records.map((record) => asArchiveItem({ kind: 'invitation', typeLabel: 'Invitation' }, record, record.email, `${record.status} · expires ${new Date(record.expiresAt).toLocaleDateString()}`))
    },
    restore: (id) => customFetch<void>(`/api/v1/invitations/${encode(id)}/restore`, { method: 'POST' }),
  },
  {
    kind: 'role',
    typeLabel: 'Role',
    readPermission: 'roles.read',
    managePermission: 'roles.manage',
    async load() {
      const records = await customFetch<RoleDto[]>('/api/v1/roles?lifecycle=recoverable', { method: 'GET' })
      return records.map((record) => asArchiveItem({ kind: 'role', typeLabel: 'Role' }, { ...record, lifecycle: record.lifecycle as RecordLifecycle }, record.name, `${record.isSystem ? 'System' : 'Custom'} role · ${record.permissions.length} permission grants`))
    },
    restore: (id) => customFetch<void>(`/api/v1/roles/${encode(id)}/restore`, { method: 'POST' }),
    requestDeletion: (id, reason) => customFetch<void>(`/api/v1/roles/${encode(id)}`, {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    }),
  },
] as const

const moduleArchiveResources: readonly ArchiveResourceDefinition[] = workspaceModules.archiveResources.map(
  (resource: ArchiveResourceContribution) => {
    if (archiveResourceDefinitions.some((core) => core.kind === resource.kind)) {
      throw new Error(`Module archive resource '${resource.kind}' conflicts with a host resource.`)
    }
    return {
      ...resource,
      async load() {
        return (await resource.load()).map((item) => ({
          ...item,
          kind: resource.kind,
          typeLabel: resource.typeLabel,
          lifecycle: item.lifecycle as RecordLifecycle,
        }))
      },
    }
  },
)

export const allArchiveResourceDefinitions: readonly ArchiveResourceDefinition[] = [
  ...archiveResourceDefinitions,
  ...moduleArchiveResources,
]

export async function loadArchiveItems(permissions: readonly string[]) {
  const readable = allArchiveResourceDefinitions.filter((definition) => permissions.includes(definition.readPermission))
  const groups = await Promise.all(readable.map((definition) => definition.load()))
  return groups.flat().sort((left, right) => archiveTimestamp(right) - archiveTimestamp(left))
}

export function canRestoreArchiveItem(item: ArchiveItem, permissions: readonly string[]) {
  return permissions.includes(allArchiveResourceDefinitions.find((definition) => definition.kind === item.kind)?.managePermission ?? '')
}

export function canRequestArchiveItemDeletion(item: ArchiveItem, permissions: readonly string[]) {
  return item.lifecycle.status === 'Archived'
    && allArchiveResourceDefinitions.find((definition) => definition.kind === item.kind)?.requestDeletion !== undefined
    && canRestoreArchiveItem(item, permissions)
}

export function archiveTimestamp(item: ArchiveItem) {
  return new Date(item.lifecycle.deletedAt ?? item.lifecycle.archivedAt ?? 0).getTime()
}

export function restoreArchiveItem(item: ArchiveItem) {
  const definition = allArchiveResourceDefinitions.find((resource) => resource.kind === item.kind)
  if (!definition) throw new Error(`Unknown archive resource '${item.kind}'.`)
  return definition.restore(item.id)
}

export function requestArchiveItemDeletion(item: ArchiveItem, reason: string) {
  const definition = allArchiveResourceDefinitions.find((resource) => resource.kind === item.kind)
  if (!definition?.requestDeletion) throw new Error(`Archive resource '${item.kind}' cannot request deletion.`)
  return definition.requestDeletion(item.id, reason)
}
