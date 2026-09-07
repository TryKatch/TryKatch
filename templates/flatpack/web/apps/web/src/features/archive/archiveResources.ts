import { customFetch, type RoleDto } from '@flatpackapp/api-client'
import type { RecordLifecycle } from '../../components/RecordLifecycle'

export type ArchiveResourceKind = 'project' | 'member' | 'invitation' | 'role'

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
  },
] as const

export async function loadArchiveItems(permissions: readonly string[]) {
  const readable = archiveResourceDefinitions.filter((definition) => permissions.includes(definition.readPermission))
  const groups = await Promise.all(readable.map((definition) => definition.load()))
  return groups.flat().sort((left, right) => archiveTimestamp(right) - archiveTimestamp(left))
}

export function canRestoreArchiveItem(item: ArchiveItem, permissions: readonly string[]) {
  return permissions.includes(archiveResourceDefinitions.find((definition) => definition.kind === item.kind)?.managePermission ?? '')
}

export function canRequestArchiveItemDeletion(item: ArchiveItem, permissions: readonly string[]) {
  return item.lifecycle.status === 'Archived' && item.kind !== 'invitation' && canRestoreArchiveItem(item, permissions)
}

export function archiveTimestamp(item: ArchiveItem) {
  return new Date(item.lifecycle.deletedAt ?? item.lifecycle.archivedAt ?? 0).getTime()
}

export function restoreArchiveItem(item: ArchiveItem) {
  const collection = item.kind === 'project' ? 'projects' : item.kind === 'member' ? 'members' : item.kind === 'invitation' ? 'invitations' : 'roles'
  return customFetch<void>(`/api/v1/${collection}/${encode(item.id)}/restore`, { method: 'POST' })
}

export function requestArchiveItemDeletion(item: ArchiveItem, reason: string) {
  const collection = item.kind === 'project' ? 'projects' : item.kind === 'member' ? 'members' : item.kind === 'invitation' ? 'invitations' : 'roles'
  return customFetch<void>(`/api/v1/${collection}/${encode(item.id)}`, {
    method: 'DELETE',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ reason }),
  })
}
