import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from '@tanstack/react-router'
import { customFetch, type AuditDto, type AuditPageDto, type PermissionModuleDto, type RoleDto } from '@trykatchapp/api-client'
import { Badge, Button, DataTable, DeleteConfirmationDialog, Dialog, EmptyState, FilterBar, PageHeader, PasswordField, RowActions, Skeleton, Surface, type DataTableColumn, type RowAction } from '@trykatchapp/ui'
import { Activity, ArrowUpRight, Building2, CheckCircle2, Clock3, Copy, FolderKanban, KeyRound, Mail, Plus, ShieldCheck, UserPlus, Users } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { RoleEditorDialog } from '../components/RoleEditorDialog'
import { RolePermissionDisclosure } from '../components/RolePermissionDisclosure'
import { formatRecordDate, LifecycleBadge, RecordDetailsDialog, type RecordLifecycle } from '../components/RecordLifecycle'
import { TrykatchLogo } from '../components/TrykatchLogo'
import { useDataTableLabels } from '../i18n/useDataTableLabels'
import { useDeleteConfirmationLabels } from '../i18n/useDeleteConfirmationLabels'
import { useI18n } from '../i18n/I18nProvider'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'

type Role = RoleDto & { lifecycle: RecordLifecycle }
interface Member { id: string; userId: string; email: string; displayName: string; status: string; joinedAt: string; roles: Role[]; lifecycle: RecordLifecycle }
interface Invitation { id: string; email: string; createdAt: string; expiresAt: string; status: string; lifecycle: RecordLifecycle }
interface InvitationCreation { invitation: Invitation; invitationUrl: string; emailDelivered: boolean }
interface OrganizationAccess { membershipId: string; permissions: string[] }
interface AccountProfile { userId: string; displayName: string; email: string; emailConfirmed: boolean; twoFactorEnabled: boolean }
type AuditEvent = AuditDto
type AuditPage = AuditPageDto
interface Organization { id: string; name: string; slug: string; isActive: boolean; createdAt: string }
interface OrganizationPage { items: Organization[]; page: number; pageSize: number; totalCount: number }
interface TenantProvisioning { organization: Organization; administratorEmail: string; invitationToken: string }
interface PlatformUser { id: string; email: string; displayName: string; roleKey: string; roleName: string; isActive: boolean; isPendingActivation: boolean; createdAt: string; lastSignedInAt?: string }
interface PlatformUserPage { items: PlatformUser[]; page: number; pageSize: number; totalCount: number }
interface PlatformRole { key: string; name: string; description: string; order: number; permissions: string[]; isSystem: boolean; canAssign: boolean }
interface PlatformAccessGrant { user: PlatformUser; activationToken?: string }
interface PlatformSession { userId: string; email?: string; isPlatformAdministrator?: boolean; platformPermissions?: string[] }
interface WorkspaceActivity { action: string; title: string; targetDisplayName: string; actorDisplayName: string; occurredAt: string }
interface WorkspaceMetric { id: string; label: string; value: number; note: string }
interface WorkspaceOverview { moduleMetrics: WorkspaceMetric[]; activeMembers: number; pendingInvitations: number; activeRoles: number; membersWithAccess: number; eventsToday: number; recentActivity: WorkspaceActivity[] }

const platformCapabilityLabels: Record<string, string> = {
  dashboard: 'Platform overview',
  tenants: 'Tenant management',
  users: 'User management',
  invitations: 'Invitations',
  authentication: 'Authentication',
}

function platformCapabilities(permissions: readonly string[]) {
  const capabilities = new Map<string, Set<string>>()
  for (const permission of permissions) {
    const [, resource, action] = permission.split('.')
    if (!resource || !action) continue
    const actions = capabilities.get(resource) ?? new Set<string>()
    actions.add(action)
    capabilities.set(resource, actions)
  }
  return [...capabilities.entries()].map(([resource, actions]) => ({
    name: platformCapabilityLabels[resource] ?? resource,
    access: actions.has('manage') ? 'Manage' : 'View',
  }))
}

function PlatformRolePicker({ roles, defaultValue }: { roles: readonly PlatformRole[]; defaultValue: string }) {
  const { t } = useI18n()
  const summaries: Record<string, string> = {
    'platform-administrator': 'Full platform access, including security settings.',
    'platform-operator': 'Manage tenants and invitations.',
    'platform-auditor': 'View platform activity without making changes.',
  }
  return <fieldset className="platform-role-picker">
    <legend>{t('Platform role')}</legend>
    <p>{t('Choose the access this person needs.')}</p>
    <div>{roles.map((role) => <label className={!role.canAssign ? 'is-disabled' : undefined} key={role.key}>
      <input type="radio" name="roleKey" value={role.key} defaultChecked={role.key === defaultValue} disabled={!role.canAssign} required />
      <span className="platform-role-picker-radio" aria-hidden="true" />
      <span className="platform-role-picker-copy"><strong>{t(role.name)}{role.key === 'platform-operator' && <span className="role-recommendation">{t('Recommended')}</span>}</strong><small>{t(summaries[role.key] ?? role.description)}</small></span>
    </label>)}</div>
  </fieldset>
}

export function DashboardPage() {
  const { locale, t } = useI18n()
  const overview = useQuery({ queryKey: ['workspace-overview'], queryFn: () => customFetch<WorkspaceOverview>('/api/v1/workspace/overview', { method: 'GET' }) })
  const data = overview.data
  const accessCoverage = data?.activeMembers ? Math.round((data.membersWithAccess / data.activeMembers) * 100) : 100
  const stats = [
    ...(data?.moduleMetrics ?? []).map((metric) => ({ ...metric, icon: FolderKanban })),
    { label: t('Members'), value: data?.activeMembers ?? 0, note: `${data?.pendingInvitations ?? 0} ${t(data?.pendingInvitations === 1 ? 'pending invitation' : 'pending invitations')}`, icon: Users },
    { label: t('Access coverage'), value: `${accessCoverage}%`, note: `${data?.activeRoles ?? 0} ${t(data?.activeRoles === 1 ? 'active access level' : 'active access levels')}`, icon: ShieldCheck },
    { label: t('Events today'), value: data?.eventsToday ?? 0, note: t('Recorded audit events'), icon: Activity },
  ]
  return <><PageHeader eyebrow={t('Workspace')} title={t('Overview')} description={t('Live activity, access, and application records for this workspace.')} actions={<Button asChild variant="primary"><Link to="/projects"><Plus size={14} /> {t('New project')}</Link></Button>} />{overview.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div> : overview.isError ? <EmptyState title={t('Workspace overview could not be loaded')} description={overview.error.message} action={<Button onClick={() => overview.refetch()}>{t('Try again')}</Button>} /> : <><div className="stat-grid">{stats.map(({ label, value, note, icon: Icon }) => <Surface className="stat" key={label}><div className="stat-top"><span>{label}</span><Icon size={15} /></div><strong>{value}</strong><small>{note}</small></Surface>)}</div><Surface><div className="panel-title"><div><h2>{t('Recent activity')}</h2><p>{t('Latest audited changes in this workspace.')}</p></div><Button asChild variant="ghost"><Link to="/audit">{t('View all')} <ArrowUpRight size={13} /></Link></Button></div>{data?.recentActivity.length ? <div className="activity-list">{data.recentActivity.map((item) => <div className="activity-row" key={`${item.action}-${item.occurredAt}`}><span className="activity-dot" /><div><strong>{item.title}: {item.targetDisplayName}</strong><small>{relativeTime(item.occurredAt, locale)} · {item.actorDisplayName}</small></div></div>)}</div> : <EmptyState title={t('No activity yet')} description={t('Audited project and access changes will appear here.')} />}</Surface></>}</>
}

export function CollectionPage({ title, description }: { title: string; description: string }) {
  return <><PageHeader eyebrow="Administration" title={title} description={description} actions={<Button variant="primary"><Plus size={14} /> Add new</Button>} /><Surface className="collection"><FilterBar /><EmptyState title={`No ${title.toLowerCase()} to show`} description="This enterprise surface is ready for its bounded application use case." /></Surface></>
}

function initials(value: string) {
  return value.split(/\s+/).filter(Boolean).map((part) => part[0]).join('').slice(0, 2).toUpperCase() || 'U'
}

function relativeTime(value: string, locale: string) {
  const elapsed = Date.now() - new Date(value).getTime()
  const minutes = Math.max(0, Math.round(elapsed / 60_000))
  const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto', style: 'short' })
  if (minutes < 1) return formatter.format(0, 'minute')
  if (minutes < 60) return formatter.format(-minutes, 'minute')
  const hours = Math.round(minutes / 60)
  if (hours < 24) return formatter.format(-hours, 'hour')
  const days = Math.round(hours / 24)
  return days < 30 ? formatter.format(-days, 'day') : new Date(value).toLocaleDateString(locale)
}

function rolePermissionMetadata(role: Role, modules: readonly PermissionModuleDto[]) {
  const granted = new Set(role.permissions)
  const matchingPermissions = modules.flatMap((module) => module.permissions.filter((permission) => granted.has(permission.key)))
  const moduleCount = modules.filter((module) => module.permissions.some((permission) => granted.has(permission.key))).length
  const knownKeys = new Set(matchingPermissions.map((permission) => permission.key))
  const unknownCount = role.permissions.filter((key) => !knownKeys.has(key)).length
  return {
    moduleCount: moduleCount + (unknownCount > 0 ? 1 : 0),
    searchText: matchingPermissions.map((permission) => `${permission.name} ${permission.description} ${permission.key}`).join(' '),
  }
}

function detailsForManagedRecord(selection: { kind: 'member' | 'invitation' | 'role'; record: Member | Invitation | Role }) {
  if (selection.kind === 'member') {
    const member = selection.record as Member
    return [
      { label: 'Display name', value: member.displayName }, { label: 'Email', value: member.email },
      { label: 'Access status', value: member.status }, { label: 'Record status', value: <LifecycleBadge lifecycle={member.lifecycle} /> },
      { label: 'Roles', value: member.roles.map((role) => role.name).join(', ') || 'No roles' }, { label: 'Joined', value: formatRecordDate(member.joinedAt) },
      ...(member.lifecycle.deletionReason ? [{ label: 'Deletion reason', value: member.lifecycle.deletionReason }] : []),
    ]
  }
  if (selection.kind === 'invitation') {
    const invitation = selection.record as Invitation
    return [
      { label: 'Recipient', value: invitation.email }, { label: 'Invitation status', value: invitation.status },
      { label: 'Record status', value: <LifecycleBadge lifecycle={invitation.lifecycle} /> }, { label: 'Created', value: formatRecordDate(invitation.createdAt) },
      { label: 'Expires', value: formatRecordDate(invitation.expiresAt) },
      ...(invitation.lifecycle.deletionReason ? [{ label: 'Deletion reason', value: invitation.lifecycle.deletionReason }] : []),
    ]
  }
  const role = selection.record as Role
  return [
    { label: 'Role name', value: role.name }, { label: 'Purpose', value: role.description || 'No description' }, { label: 'Type', value: role.isSystem ? 'System role' : 'Custom role' },
    { label: 'Record status', value: <LifecycleBadge lifecycle={role.lifecycle} /> }, { label: 'Permission grants', value: role.permissions.length },
    { label: 'Permissions', value: role.permissions.join(', ') || 'No permissions' },
    ...(role.lifecycle.deletionReason ? [{ label: 'Deletion reason', value: role.lifecycle.deletionReason }] : []),
  ]
}

export function UserManagementPage() {
  const { t } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const deleteConfirmationLabels = useDeleteConfirmationLabels()
  const client = useQueryClient()
  const [tab, setTab] = useState<'members' | 'invitations' | 'roles'>('members')
  const [roleType, setRoleType] = useState<'all' | 'system' | 'custom'>('all')
  const [inviteOpen, setInviteOpen] = useState(false)
  const [invitationResult, setInvitationResult] = useState<InvitationCreation>()
  const [invitationCopied, setInvitationCopied] = useState(false)
  const [editingRole, setEditingRole] = useState<Role | null | undefined>(undefined)
  const [editingMember, setEditingMember] = useState<Member>()
  const [editingInvitation, setEditingInvitation] = useState<Invitation>()
  const [viewing, setViewing] = useState<{ kind: 'member' | 'invitation' | 'role'; record: Member | Invitation | Role }>()
  const [deletingInvitation, setDeletingInvitation] = useState<Invitation>()
  const access = useQuery({ queryKey: ['access'], queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }) })
  const members = useQuery({ queryKey: ['members', 'active'], queryFn: () => customFetch<Member[]>('/api/v1/members?lifecycle=active', { method: 'GET' }) })
  const invitations = useQuery({ queryKey: ['invitations', 'active'], queryFn: () => customFetch<Invitation[]>('/api/v1/invitations?lifecycle=active', { method: 'GET' }) })
  const roles = useQuery({ queryKey: ['access-levels', 'active'], queryFn: () => customFetch<Role[]>('/api/v1/roles?lifecycle=active', { method: 'GET' }) })
  const activeRoles = useQuery({ queryKey: ['access-levels', 'active'], queryFn: () => customFetch<Role[]>('/api/v1/roles?lifecycle=active', { method: 'GET' }) })
  const permissionCatalog = useQuery({ queryKey: ['permission-catalog'], queryFn: () => customFetch<PermissionModuleDto[]>('/api/v1/permissions', { method: 'GET' }) })
  const invite = useMutation({
    mutationFn: (email: string) => customFetch<InvitationCreation>('/api/v1/invitations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, expiresInDays: 7 }) }),
    onSuccess: async (result) => { setInvitationResult(result); setInvitationCopied(false); await client.invalidateQueries({ queryKey: ['invitations'] }) },
  })
  const updateMember = useMutation({
    mutationFn: ({ member, roleIds, isActive }: { member: Member; roleIds: string[]; isActive: boolean }) => customFetch<Member>(`/api/v1/members/${member.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ membershipId: member.id, roleIds, isActive }) }),
    onSuccess: async () => { setEditingMember(undefined); await client.invalidateQueries({ queryKey: ['members'] }) },
  })
  const revoke = useMutation({
    mutationFn: (id: string) => customFetch<void>(`/api/v1/invitations/${id}/revoke`, { method: 'POST' }),
    onSuccess: async () => { setEditingInvitation(undefined); await client.invalidateQueries({ queryKey: ['invitations'] }) },
  })
  const updateInvitation = useMutation({
    mutationFn: ({ id, expiresInDays }: { id: string; expiresInDays: number }) => customFetch<Invitation>(`/api/v1/invitations/${id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expiresInDays }) }),
    onSuccess: async () => { setEditingInvitation(undefined); await client.invalidateQueries({ queryKey: ['invitations'] }) },
  })
  const saveRole = useMutation({
    mutationFn: (input: { id?: string; name: string; description: string; permissions: string[] }) => customFetch<Role>(
      input.id ? `/api/v1/roles/${input.id}` : '/api/v1/roles',
      { method: input.id ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: input.id ?? null, name: input.name, description: input.description, permissions: input.permissions }) },
    ),
    onSuccess: async () => { setEditingRole(undefined); await client.invalidateQueries({ queryKey: ['access-levels'] }) },
  })
  const archiveRecord = useMutation({
    mutationFn: ({ kind, id }: { kind: 'members' | 'roles'; id: string }) => customFetch<void>(`/api/v1/${kind}/${id}/archive`, { method: 'POST' }),
    onSuccess: async () => Promise.all([
      client.invalidateQueries({ queryKey: ['members'] }),
      client.invalidateQueries({ queryKey: ['access-levels'] }),
    ]),
  })
  const deleteInvitation = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => customFetch<void>(`/api/v1/invitations/${id}`, { method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }) }),
    onSuccess: async () => { setDeletingInvitation(undefined); await client.invalidateQueries({ queryKey: ['invitations'] }) },
  })
  function submitInvite(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); invite.mutate(String(data.get('email'))) }
  function submitMember(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!editingMember) return; const data = new FormData(event.currentTarget); updateMember.mutate({ member: editingMember, roleIds: data.getAll('roleIds').map(String), isActive: data.get('status') === 'Active' }) }
  function submitInvitation(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!editingInvitation) return; const data = new FormData(event.currentTarget); updateInvitation.mutate({ id: editingInvitation.id, expiresInDays: Number(data.get('expiresInDays')) }) }
  const canManageMembers = access.data?.permissions.includes('members.manage') ?? false
  const canManageRoles = access.data?.permissions.includes('roles.manage') ?? false
  const openInvite = () => { setInvitationResult(undefined); setInvitationCopied(false); invite.reset(); setInviteOpen(true) }
  async function copyInvitationLink() {
    if (!invitationResult) return
    await navigator.clipboard.writeText(invitationResult.invitationUrl)
    setInvitationCopied(true)
  }
  const changeTab = (nextTab: 'members' | 'invitations' | 'roles') => { setTab(nextTab); setRoleType('all') }
  const headerAction = tab === 'roles'
    ? canManageRoles ? <Button variant="primary" onClick={() => setEditingRole(null)}><Plus size={14} /> {t('New role')}</Button> : undefined
    : canManageMembers ? <Button variant="primary" onClick={openInvite}><UserPlus size={14} /> {t('Invite person')}</Button> : undefined
  const roleToolbar = <select aria-label={t('Filter roles by type')} value={roleType} onChange={(event) => setRoleType(event.target.value as typeof roleType)}><option value="all">{t('All role types')}</option><option value="system">{t('System roles')}</option><option value="custom">{t('Custom roles')}</option></select>
  const displayedRoles = (roles.data ?? []).filter((role) => roleType === 'all' || (roleType === 'system' ? role.isSystem : !role.isSystem))
  const memberActions = (member: Member): RowAction[] => {
    const isCurrentMember = access.data?.membershipId === member.id
    const actions: RowAction[] = [{ label: t('View'), icon: 'view', onSelect: () => setViewing({ kind: 'member', record: member }) }]
    if (!canManageMembers || isCurrentMember) return actions
    actions.push(
      { label: t('Edit'), icon: 'edit', onSelect: () => setEditingMember(member) },
      { label: t('Archive'), icon: 'archive', onSelect: () => archiveRecord.mutate({ kind: 'members', id: member.id }) },
    )
    return actions
  }
  const invitationActions = (invitation: Invitation): RowAction[] => {
    const actions: RowAction[] = [{ label: t('View'), icon: 'view', onSelect: () => setViewing({ kind: 'invitation', record: invitation }) }]
    if (!canManageMembers) return actions
    actions.push(
      ...(invitation.status === 'Pending' ? [{ label: t('Edit'), icon: 'edit' as const, onSelect: () => setEditingInvitation(invitation) }] : []),
      ...(invitation.status === 'Pending' ? [{ label: t('Revoke'), icon: 'revoke' as const, onSelect: () => revoke.mutate(invitation.id) }] : []),
      { label: t('Delete'), icon: 'delete', danger: true, onSelect: () => setDeletingInvitation(invitation) },
    )
    return actions
  }
  const roleActions = (role: Role): RowAction[] => {
    const actions: RowAction[] = [{ label: t('View'), icon: 'view', onSelect: () => setViewing({ kind: 'role', record: role }) }]
    if (!canManageRoles || role.isSystem || !role.canAssign) return actions
    actions.push(
      { label: t('Edit'), icon: 'edit', onSelect: () => setEditingRole(role) },
      { label: t('Archive'), icon: 'archive', onSelect: () => archiveRecord.mutate({ kind: 'roles', id: role.id }) },
    )
    return actions
  }
  const memberColumns: DataTableColumn<Member>[] = [
    { id: 'person', header: t('Person'), hideable: false, cell: (member) => <div className="user-cell"><span className="user-avatar">{initials(member.displayName || member.email)}</span><span><strong>{member.displayName}</strong><small>{member.email}</small></span></div>, sortValue: (member) => member.displayName, searchValue: (member) => `${member.displayName} ${member.email}` },
    { id: 'access', header: t('Access'), cell: (member) => member.roles.map((role) => role.name).join(', ') || t('No role'), sortValue: (member) => member.roles.map((role) => role.name).join(' ') },
    { id: 'status', header: t('Status'), cell: (member) => member.lifecycle.status === 'Active' ? <Badge tone={member.status === 'Active' ? 'success' : 'warning'}>{t(member.status)}</Badge> : <LifecycleBadge lifecycle={member.lifecycle} />, sortValue: (member) => `${member.lifecycle.status}-${member.status}` },
    { id: 'joined', header: t('Joined'), cell: (member) => new Date(member.joinedAt).toLocaleDateString(), sortValue: (member) => new Date(member.joinedAt) },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (member) => <RowActions label={t('Actions for {name}', { name: member.displayName })} actions={memberActions(member)} /> },
  ]
  const invitationColumns: DataTableColumn<Invitation>[] = [
    { id: 'email', header: t('Email'), hideable: false, cell: (invitation) => invitation.email, sortValue: (invitation) => invitation.email, searchValue: (invitation) => invitation.email },
    { id: 'expires', header: t('Expires'), cell: (invitation) => new Date(invitation.expiresAt).toLocaleDateString(), sortValue: (invitation) => new Date(invitation.expiresAt) },
    { id: 'status', header: t('Status'), cell: (invitation) => invitation.lifecycle.status === 'Active' ? <Badge tone={invitation.status === 'Pending' ? 'info' : 'neutral'}>{t(invitation.status)}</Badge> : <LifecycleBadge lifecycle={invitation.lifecycle} />, sortValue: (invitation) => `${invitation.lifecycle.status}-${invitation.status}` },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (invitation) => <RowActions label={t('Actions for invitation to {email}', { email: invitation.email })} actions={invitationActions(invitation)} /> },
  ]
  const roleColumns: DataTableColumn<Role>[] = [
    { id: 'name', header: t('Role'), hideable: false, cell: (role) => <div className="role-name-cell"><strong>{role.name}</strong><small>{role.description || t(role.isSystem ? 'Managed by Trykatch' : 'Custom workspace access')}</small></div>, sortValue: (role) => role.name, searchValue: (role) => `${role.name} ${role.description} ${role.permissions.join(' ')} ${rolePermissionMetadata(role, permissionCatalog.data ?? []).searchText}` },
    { id: 'type', header: t('Type'), cell: (role) => role.lifecycle.status === 'Active' ? <Badge tone={role.isSystem ? 'neutral' : 'info'}>{t(role.isSystem ? 'System' : 'Custom')}</Badge> : <LifecycleBadge lifecycle={role.lifecycle} />, sortValue: (role) => `${role.lifecycle.status}-${role.isSystem ? 'System' : 'Custom'}` },
    { id: 'permissions', header: t('Permissions'), cell: (role) => { const metadata = rolePermissionMetadata(role, permissionCatalog.data ?? []); return <div className="role-grant-summary"><strong>{role.permissions.length}</strong><small>{t(role.permissions.length === 1 ? 'grant' : 'grants')} · {metadata.moduleCount} {t(metadata.moduleCount === 1 ? 'module' : 'modules')}</small></div> }, sortValue: (role) => role.permissions.length },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (role) => <RowActions label={t('Actions for {name}', { name: role.name })} actions={roleActions(role)} /> },
  ]
  return <>
    <PageHeader eyebrow={t('Manage')} title={t('User Management')} description={t('Manage people, invitations, roles, and organization access.')} actions={headerAction} />
    <div className="user-management-tabs" role="tablist" aria-label={t('User management')}>
      <button role="tab" aria-selected={tab === 'members'} className={tab === 'members' ? 'active' : ''} onClick={() => changeTab('members')}><Users size={16} /> {t('People')} <Badge>{members.data?.length ?? 0}</Badge></button>
      <button role="tab" aria-selected={tab === 'invitations'} className={tab === 'invitations' ? 'active' : ''} onClick={() => changeTab('invitations')}><Mail size={16} /> {t('Invitations')} <Badge>{invitations.data?.length ?? 0}</Badge></button>
      <button role="tab" aria-selected={tab === 'roles'} className={tab === 'roles' ? 'active' : ''} onClick={() => changeTab('roles')}><ShieldCheck size={16} /> {t('Roles')} <Badge>{roles.data?.length ?? 0}</Badge></button>
    </div>
    {(members.error || invitations.error || roles.error || permissionCatalog.error || updateMember.error || updateInvitation.error || revoke.error || archiveRecord.error) && <div className="page-alert" role="alert">{(members.error ?? invitations.error ?? roles.error ?? permissionCatalog.error ?? updateMember.error ?? updateInvitation.error ?? revoke.error ?? archiveRecord.error)?.message}</div>}
    {tab === 'members' ? <Surface className="collection">{members.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : <DataTable labels={dataTableLabels} ariaLabel={t('Organization members')} data={members.data ?? []} columns={memberColumns} getRowId={(member) => member.id} searchPlaceholder={t('Search people…')} initialSort={{ id: 'person', direction: 'asc' }} empty={<EmptyState title={t('No people')} description={t('Active organization memberships appear here.')} />}/>}</Surface>
      : tab === 'invitations' ? <Surface className="collection"><div className="panel-heading"><div><h2>{t('Organization invitations')}</h2><p>{t('Invited people join with Member access by default.')}</p></div></div>{invitations.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : <DataTable labels={dataTableLabels} ariaLabel={t('Organization invitations')} data={invitations.data ?? []} columns={invitationColumns} getRowId={(invitation) => invitation.id} searchPlaceholder={t('Search invitations…')} initialSort={{ id: 'expires', direction: 'asc' }} empty={<EmptyState title={t('No invitations')} description={t('Active invitation records appear here.')} />}/>}</Surface>
        : <Surface className="collection"><div className="panel-heading"><div><h2>{t('Organization roles')}</h2><p>{t('Expand a role to inspect its permissions.')}</p></div></div>{roles.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : <DataTable labels={dataTableLabels} ariaLabel={t('Organization roles')} data={displayedRoles} columns={roleColumns} getRowId={(role) => role.id} searchPlaceholder={t('Search roles or permissions…')} toolbar={roleToolbar} initialSort={{ id: 'name', direction: 'asc' }} pageSize={20} getRowExpansionLabel={(role) => role.name} renderExpandedRow={(role) => <RolePermissionDisclosure roleName={role.name} permissionKeys={role.permissions} modules={permissionCatalog.data ?? []} isLoading={permissionCatalog.isLoading} />} empty={<EmptyState title={t('No roles')} description={t('Adjust the role type or search to see more roles.')} />}/>}</Surface>}
    <Dialog open={inviteOpen} onOpenChange={setInviteOpen} title={t(invitationResult ? 'Invitation ready' : 'Invite a member')} description={t('The invitation is valid for seven days and can be accepted once.')}>{invitationResult ? <div className="token-result"><div className="delete-record-summary"><span>{t('Recipient')}</span><strong>{invitationResult.invitation.email}</strong></div><p>{t(invitationResult.emailDelivered ? 'The invitation was emailed successfully. You can also copy the link below.' : 'Email delivery is not configured. Copy this link and share it with the invited person through a secure channel.')}</p><code>{invitationResult.invitationUrl}</code><Button variant="secondary" onClick={copyInvitationLink}><Copy size={14} /> {t(invitationCopied ? 'Link copied' : 'Copy invitation link')}</Button><div className="dialog-actions"><Button variant="primary" onClick={() => setInviteOpen(false)}>{t('Done')}</Button></div></div> : <form className="dialog-form" onSubmit={submitInvite}><label>{t('Email address')}<input name="email" type="email" autoComplete="email" required autoFocus /></label><small>{t('The person will use the invitation link to create an account or sign in.')}</small>{invite.error && <div className="form-error" role="alert">{invite.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setInviteOpen(false)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={invite.isPending}>{t(invite.isPending ? 'Creating…' : 'Create invitation')}</Button></div></form>}</Dialog>
    <RoleEditorDialog open={editingRole !== undefined} role={editingRole} modules={permissionCatalog.data ?? []} isLoading={permissionCatalog.isLoading} isSaving={saveRole.isPending} error={saveRole.error?.message} onOpenChange={(open) => { if (!open) { setEditingRole(undefined); saveRole.reset() } }} onSave={(value) => saveRole.mutate({ id: editingRole?.id, ...value })} />
    <Dialog open={editingMember !== undefined} onOpenChange={(open) => !open && setEditingMember(undefined)} title={t('Edit member')} description={t('Assign one or more roles and control organization access.')}>{editingMember && <form className="dialog-form" onSubmit={submitMember}><div className="delete-record-summary"><span>{t('Member')}</span><strong>{editingMember.displayName}</strong><small>{editingMember.email}</small></div><fieldset className="role-assignment"><legend>{t('Roles')}</legend>{activeRoles.data?.map((role) => <label className="checkbox" key={role.id}><input type="checkbox" name="roleIds" value={role.id} defaultChecked={editingMember.roles.some((assigned) => assigned.id === role.id)} disabled={!role.canAssign} /> <span>{role.name}{role.isSystem ? ` · ${t('System')}` : ''}</span></label>)}</fieldset><label>{t('Status')}<select name="status" defaultValue={editingMember.status}><option value="Active">{t('Active')}</option><option value="Suspended">{t('Suspended')}</option></select></label>{updateMember.error && <div className="form-error" role="alert">{updateMember.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setEditingMember(undefined)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={updateMember.isPending}>{t('Save member')}</Button></div></form>}</Dialog>
    <Dialog open={editingInvitation !== undefined} onOpenChange={(open) => !open && setEditingInvitation(undefined)} title={t('Edit invitation')} description={t('Extend a pending invitation without changing its recipient.')}>{editingInvitation && <form className="dialog-form" onSubmit={submitInvitation}><div className="delete-record-summary"><span>{t('Recipient')}</span><strong>{editingInvitation.email}</strong></div><label>{t('Expires in days')}<input name="expiresInDays" type="number" min="1" max="30" defaultValue="7" required /></label>{updateInvitation.error && <div className="form-error" role="alert">{updateInvitation.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="danger" disabled={revoke.isPending} onClick={() => revoke.mutate(editingInvitation.id)}>{t('Revoke invitation')}</Button><Button type="button" variant="ghost" onClick={() => setEditingInvitation(undefined)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={updateInvitation.isPending}>{t('Save invitation')}</Button></div></form>}</Dialog>
    <RecordDetailsDialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing ? viewing.kind === 'member' ? (viewing.record as Member).displayName : viewing.kind === 'invitation' ? (viewing.record as Invitation).email : (viewing.record as Role).name : t('Record')} description={t('Current values and recoverable lifecycle information.')} recordType={t(viewing ? viewing.kind === 'member' ? 'Person' : viewing.kind === 'invitation' ? 'Invitation' : 'Role' : 'Record')} status={viewing ? <LifecycleBadge lifecycle={viewing.record.lifecycle} /> : undefined} details={viewing ? detailsForManagedRecord(viewing) : []} />
    <DeleteConfirmationDialog open={deletingInvitation !== undefined} onOpenChange={(open) => !open && setDeletingInvitation(undefined)} recordType={t('invitation')} recordName={deletingInvitation?.email ?? ''} isDeleting={deleteInvitation.isPending} error={deleteInvitation.error?.message} labels={deleteConfirmationLabels} onConfirm={(reason) => deletingInvitation && deleteInvitation.mutate({ id: deletingInvitation.id, reason })} />
  </>
}

export function AuditPage() {
  const { locale, t } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const [page, setPage] = useState(1)
  const [filters, setFilters] = useState({ search: '', action: '', subjectType: '', actorId: '', period: 'all' })
  const audit = useQuery({
    queryKey: ['audit', page, filters],
    queryFn: () => {
      const query = new URLSearchParams({ page: String(page), pageSize: '50' })
      if (filters.search.trim()) query.set('search', filters.search.trim())
      if (filters.action) query.set('action', filters.action)
      if (filters.subjectType) query.set('subjectType', filters.subjectType)
      if (filters.actorId) query.set('actorId', filters.actorId)
      if (filters.period !== 'all') {
        const hours = filters.period === '24h' ? 24 : filters.period === '7d' ? 168 : 720
        query.set('from', new Date(Date.now() - hours * 60 * 60 * 1000).toISOString())
      }
      return customFetch<AuditPage>(`/api/v1/audit?${query}`, { method: 'GET' })
    },
  })
  const changeFilter = (name: keyof typeof filters, value: string) => { setPage(1); setFilters((current) => ({ ...current, [name]: value })) }
  const activeFilterCount = Object.values(filters).filter((value) => value && value !== 'all').length
  const columns: DataTableColumn<AuditEvent>[] = [
    { id: 'activity', header: t('Activity'), hideable: false, width: '42%', cell: (event) => <div className="audit-event"><span className={`audit-event-icon audit-${event.category.toLowerCase()}`}><Activity size={15} /></span><span><strong>{event.title}</strong><small>{event.description}</small></span></div>, sortValue: (event) => event.title, searchValue: (event) => `${event.title} ${event.description} ${event.action}` },
    { id: 'category', header: t('Category'), cell: (event) => <Badge tone={event.severity === 'Warning' ? 'warning' : 'neutral'}>{t(event.category)}</Badge>, sortValue: (event) => event.category },
    { id: 'actor', header: t('Actor'), cell: (event) => <div className="audit-identity"><span className="user-avatar">{initials(event.actor.displayName)}</span><span><strong>{event.actor.displayName}</strong><small>{event.actor.email}</small></span></div>, sortValue: (event) => event.actor.displayName, searchValue: (event) => `${event.actor.displayName} ${event.actor.email}` },
    { id: 'target', header: t('Resource'), cell: (event) => <div className="audit-target"><strong>{event.target.displayName}</strong><small>{t(event.target.type)} · {event.target.id.slice(0, 8)}</small></div>, sortValue: (event) => event.target.displayName, searchValue: (event) => `${event.target.displayName} ${event.target.type} ${event.target.id}` },
    { id: 'occurred', header: t('Time'), hideable: false, cell: (event) => <div className="audit-time"><strong>{relativeTime(event.occurredAt, locale)}</strong><small>{new Date(event.occurredAt).toLocaleString(locale)}</small></div>, sortValue: (event) => new Date(event.occurredAt) },
    { id: 'details', header: t('Details'), defaultVisible: false, cell: (event) => Object.keys(event.details).length ? <dl className="audit-details">{Object.entries(event.details).map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{value}</dd></div>)}</dl> : <span className="muted-value">{t('No additional details')}</span>, searchValue: (event) => Object.entries(event.details).flat().join(' ') },
    { id: 'technical', header: t('Event key'), defaultVisible: false, cell: (event) => <code className="event-key">{event.action}</code>, sortValue: (event) => event.action, searchValue: (event) => event.action },
  ]
  const filterOptions = audit.data?.filters
  const totalCount = Number(audit.data?.totalCount ?? 0)
  const lastPage = Math.max(1, Math.ceil(totalCount / 50))
  const tableFilters = <>
    <input className="audit-search" type="search" aria-label={t('Search audit activity')} placeholder={t('Search events or resources…')} value={filters.search} onChange={(event) => changeFilter('search', event.target.value)} />
    <select aria-label={t('Filter by action')} value={filters.action} onChange={(event) => changeFilter('action', event.target.value)}><option value="">{t('All actions')}</option>{filterOptions?.actions.map((action) => <option key={action.value} value={action.value}>{action.label}</option>)}</select>
    <select aria-label={t('Filter by resource')} value={filters.subjectType} onChange={(event) => changeFilter('subjectType', event.target.value)}><option value="">{t('All resources')}</option>{filterOptions?.subjectTypes.map((type) => <option key={type} value={type}>{t(type)}</option>)}</select>
    <select aria-label={t('Filter by actor')} value={filters.actorId} onChange={(event) => changeFilter('actorId', event.target.value)}><option value="">{t('All actors')}</option>{filterOptions?.actors.map((actor) => <option key={actor.value} value={actor.value}>{actor.label}</option>)}</select>
    <select aria-label={t('Filter by time')} value={filters.period} onChange={(event) => changeFilter('period', event.target.value)}><option value="all">{t('Any time')}</option><option value="24h">{t('Last 24 hours')}</option><option value="7d">{t('Last 7 days')}</option><option value="30d">{t('Last 30 days')}</option></select>
    {activeFilterCount > 0 && <Button variant="ghost" onClick={() => { setPage(1); setFilters({ search: '', action: '', subjectType: '', actorId: '', period: 'all' }) }}>{t('Clear {count}', { count: activeFilterCount })}</Button>}
  </>
  return <>
    <PageHeader eyebrow={t('Security')} title={t('Audit activity')} description={t('A readable, immutable history of important organization changes.')} actions={<Button variant="secondary" onClick={() => audit.refetch()}><Clock3 size={14} /> {t('Refresh')}</Button>} />
    <Surface className="collection audit-collection">
      {audit.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div> : audit.isError ? <EmptyState title={t('Audit activity could not be loaded')} description={audit.error.message} action={<Button onClick={() => audit.refetch()}>{t('Try again')}</Button>} /> : audit.data?.items.length ? <>
        <DataTable labels={dataTableLabels} ariaLabel={t('Audit activity')} data={audit.data.items} columns={columns} getRowId={(event) => event.id} searchable={false} toolbar={tableFilters} initialSort={{ id: 'occurred', direction: 'desc' }} initialDensity="comfortable" />
        <div className="table-pagination"><span>{t('Showing {start}–{end} of {total} events', { start: (page - 1) * 50 + 1, end: Math.min(page * 50, totalCount), total: totalCount })}</span><div><Button variant="secondary" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>{t('Previous')}</Button><span>{t('Page {page} of {count}', { page, count: lastPage })}</span><Button variant="secondary" disabled={page >= lastPage} onClick={() => setPage((value) => value + 1)}>{t('Next')}</Button></div></div>
      </> : <><div className="audit-empty-filters">{tableFilters}</div><EmptyState title={t(activeFilterCount ? 'No matching activity' : 'No audit activity')} description={t(activeFilterCount ? 'Adjust or clear the filters to see more events.' : 'Important organization changes will appear here.')} /></>}
    </Surface>
  </>
}

export function ProfilePage() {
  const { t } = useI18n()
  const client = useQueryClient()
  const profile = useQuery({ queryKey: ['account', 'profile'], queryFn: () => customFetch<AccountProfile>('/api/v1/account', { method: 'GET' }) })
  const [setup, setSetup] = useState<{ sharedKey: string; authenticatorUri: string }>()
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>()
  const [passwordError, setPasswordError] = useState<string>()
  const updateProfile = useMutation({
    mutationFn: (displayName: string) => customFetch<AccountProfile>('/api/v1/account', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ displayName }) }),
    onSuccess: async (result) => {
      client.setQueryData(['account', 'profile'], result)
      await client.invalidateQueries({ queryKey: ['me', 'session'] })
    },
  })
  const changePassword = useMutation({
    mutationFn: (input: { currentPassword: string; newPassword: string }) => customFetch<void>('/api/v1/account/password', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) }),
    onSuccess: () => window.location.assign('/login'),
  })
  const beginMfa = useMutation({ mutationFn: () => customFetch<{ sharedKey: string; authenticatorUri: string }>('/api/v1/account/security/mfa/setup', { method: 'POST' }), onSuccess: setSetup })
  const enableMfa = useMutation({ mutationFn: (code: string) => customFetch<{ codes: string[] }>('/api/v1/account/security/mfa/enable', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) }), onSuccess: async (result) => { setRecoveryCodes(result.codes); setSetup(undefined); await client.invalidateQueries({ queryKey: ['account', 'profile'] }) } })
  const regenerateCodes = useMutation({ mutationFn: () => customFetch<{ codes: string[] }>('/api/v1/account/security/mfa/recovery-codes', { method: 'POST' }), onSuccess: (result) => setRecoveryCodes(result.codes) })
  function enable(event: FormEvent<HTMLFormElement>) { event.preventDefault(); enableMfa.mutate(String(new FormData(event.currentTarget).get('code'))) }
  function saveProfile(event: FormEvent<HTMLFormElement>) { event.preventDefault(); updateProfile.mutate(String(new FormData(event.currentTarget).get('displayName'))) }
  function savePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setPasswordError(undefined)
    const data = new FormData(event.currentTarget)
    const currentPassword = String(data.get('currentPassword'))
    const newPassword = String(data.get('newPassword'))
    if (newPassword !== String(data.get('confirmPassword'))) { setPasswordError(t('New passwords do not match.')); return }
    changePassword.mutate({ currentPassword, newPassword })
  }
  const identity = profile.data?.displayName || profile.data?.email || t('Account')
  return <>
    <PageHeader eyebrow={t('Account')} title={t('Profile')} description={t('Your identity, password, and sign-in security.')} />
    {profile.isError && <div className="page-alert" role="alert">{profile.error.message}</div>}
    <div className="profile-stack">
      <Surface className="profile-card">
        <div className="profile-card-heading"><div><h2>{t('Profile picture')}</h2><p>{t('Your recognizable identity across the workspace.')}</p></div></div>
        <div className="profile-avatar-row"><span className="profile-avatar">{initials(identity)}</span><div><strong>{identity}</strong><span>{t('Initials update automatically from your display name.')}</span></div></div>
      </Surface>
      <Surface className="profile-card">
        <div className="profile-card-heading"><div><h2>{t('Profile details')}</h2><p>{t('Your display name and verified sign-in address.')}</p></div>{updateProfile.isSuccess && <span className="saved-state"><CheckCircle2 size={14} /> {t('Saved')}</span>}</div>
        <form className="profile-form two-columns" key={profile.data?.displayName} onSubmit={saveProfile}>
          <label>{t('Display name')} <span aria-hidden="true">*</span><input name="displayName" defaultValue={profile.data?.displayName} maxLength={120} required /></label>
          <label>{t('Email address')} <span className="verified-label">{t(profile.data?.emailConfirmed ? 'Verified' : 'Unverified')}</span><input value={profile.data?.email ?? ''} readOnly aria-readonly="true" /></label>
          {updateProfile.error && <div className="form-error form-span" role="alert">{updateProfile.error.message}</div>}
          <div className="form-actions form-span"><Button type="submit" variant="primary" disabled={updateProfile.isPending}>{t(updateProfile.isPending ? 'Saving…' : 'Save profile')}</Button></div>
        </form>
      </Surface>
      <Surface className="profile-card">
        <div className="profile-card-heading"><div><h2>{t('Password')}</h2><p>{t('Changing your password signs out every active session.')}</p></div><KeyRound size={18} /></div>
        <form className="profile-form password-grid" onSubmit={savePassword}>
          <input className="sr-only" type="email" value={profile.data?.email ?? ''} autoComplete="username" readOnly tabIndex={-1} aria-hidden="true" />
          <PasswordField label={t('Current password')} name="currentPassword" autoComplete="current-password" visibilityLabel={t('Current password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />
          <PasswordField label={t('New password')} name="newPassword" autoComplete="new-password" minLength={12} visibilityLabel={t('New password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />
          <PasswordField label={t('Confirm new password')} name="confirmPassword" autoComplete="new-password" minLength={12} visibilityLabel={t('Confirm new password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />
          {(passwordError || changePassword.error) && <div className="form-error form-span" role="alert">{passwordError ?? changePassword.error?.message}</div>}
          <div className="form-actions form-span"><Button type="submit" variant="secondary" disabled={changePassword.isPending}>{t(changePassword.isPending ? 'Changing…' : 'Change password')}</Button></div>
        </form>
      </Surface>
      <Surface className="profile-card">
        <div className="profile-card-heading"><div><h2>{t('Two-factor authentication')}</h2><p>{t('Add a time-based one-time password to protect your account.')}</p></div><Badge tone={profile.data?.twoFactorEnabled ? 'success' : 'warning'}>{t(profile.data?.twoFactorEnabled ? 'Enabled' : 'Not enabled')}</Badge></div>
        <div className="security-panel">{recoveryCodes ? <div className="token-result"><p>Store these recovery codes securely. Each code can be used once.</p><code>{recoveryCodes.join('\n')}</code><Button variant="secondary" onClick={() => navigator.clipboard.writeText(recoveryCodes.join('\n'))}>Copy recovery codes</Button></div> : setup ? <form className="dialog-form" onSubmit={enable}><p>Enter this key in your authenticator: <code>{setup.sharedKey}</code></p><label>Six-digit code<input name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,8}" required /></label>{enableMfa.error && <div className="form-error" role="alert">{enableMfa.error.message}</div>}<div className="form-actions"><Button variant="ghost" type="button" onClick={() => setSetup(undefined)}>Cancel</Button><Button variant="primary" type="submit" disabled={enableMfa.isPending}>Enable MFA</Button></div></form> : profile.data?.twoFactorEnabled ? <div className="security-status"><div><ShieldCheck size={18} /><span><strong>Authenticator app is active</strong><small>Recovery codes provide emergency account access.</small></span></div><Button variant="secondary" disabled={regenerateCodes.isPending} onClick={() => regenerateCodes.mutate()}>Regenerate recovery codes</Button></div> : <div className="security-status"><div><ShieldCheck size={18} /><span><strong>Protect your account</strong><small>Use any standards-based TOTP authenticator.</small></span></div><Button variant="primary" disabled={beginMfa.isPending} onClick={() => beginMfa.mutate()}>Set up MFA</Button></div>}</div>
      </Surface>
    </div>
  </>
}

export function PlatformOrganizationsPage() {
  const { locale, t } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const client = useQueryClient()
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<Organization>()
  const [viewing, setViewing] = useState<Organization>()
  const [changingStatus, setChangingStatus] = useState<Organization>()
  const [provisioned, setProvisioned] = useState<TenantProvisioning>()
  const [statusFilter, setStatusFilter] = useState<'all' | 'active' | 'inactive'>('all')
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<PlatformSession>('/api/v1/auth/session', { method: 'GET' }) })
  const canManage = session.data?.isPlatformAdministrator === true || session.data?.platformPermissions?.includes('platform.tenants.manage') === true
  const organizations = useQuery({ queryKey: ['platform', 'tenants'], queryFn: () => customFetch<OrganizationPage>('/api/v1/tenants?page=1&pageSize=100', { method: 'GET' }) })
  const create = useMutation({ mutationFn: (input: { name: string; slug: string; administratorEmail: string }) => customFetch<TenantProvisioning>('/api/v1/tenants', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) }), onSuccess: async (result) => { setProvisioned(result); await client.invalidateQueries({ queryKey: ['platform', 'tenants'] }) } })
  const update = useMutation({ mutationFn: ({ id, name }: { id: string; name: string }) => customFetch<Organization>(`/api/v1/tenants/${id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) }), onSuccess: async (organization) => { setEditing(undefined); setViewing((current) => current?.id === organization.id ? organization : current); await client.invalidateQueries({ queryKey: ['platform', 'tenants'] }) } })
  const setStatus = useMutation({ mutationFn: (organization: Organization) => customFetch<Organization>(`/api/v1/tenants/${organization.id}/${organization.isActive ? 'deactivate' : 'reactivate'}`, { method: 'POST' }), onSuccess: async (organization) => { setChangingStatus(undefined); setViewing((current) => current?.id === organization.id ? organization : current); await client.invalidateQueries({ queryKey: ['platform', 'tenants'] }) } })
  function submitCreate(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); create.mutate({ name: String(data.get('name')), slug: String(data.get('slug')), administratorEmail: String(data.get('administratorEmail')) }) }
  function invitationLink(token: string) { return `${window.location.origin}/invite/${encodeURIComponent(token)}` }
  function submitEdit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!editing) return; const data = new FormData(event.currentTarget); update.mutate({ id: editing.id, name: String(data.get('name')) }) }
  const allOrganizations = organizations.data?.items ?? []
  const displayedOrganizations = allOrganizations.filter((organization) => statusFilter === 'all' || organization.isActive === (statusFilter === 'active'))
  const activeCount = allOrganizations.filter((organization) => organization.isActive).length
  const statusToolbar = <select aria-label={t('Filter tenants by status')} value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}><option value="all">{t('All statuses')}</option><option value="active">{t('Active')}</option><option value="inactive">{t('Inactive')}</option></select>
  const columns: DataTableColumn<Organization>[] = [
    { id: 'name', header: t('Tenant'), hideable: false, cell: (organization) => <div className="tenant-cell"><span className="tenant-mark" aria-hidden="true">{initials(organization.name)}</span><span><strong>{organization.name}</strong><small>{organization.slug}</small></span></div>, sortValue: (organization) => organization.name, searchValue: (organization) => `${organization.name} ${organization.slug}` },
    { id: 'slug', header: t('Workspace handle'), cell: (organization) => <code className="event-key">{organization.slug}</code>, sortValue: (organization) => organization.slug },
    { id: 'status', header: t('Status'), cell: (organization) => <Badge tone={organization.isActive ? 'success' : 'warning'}>{t(organization.isActive ? 'Active' : 'Inactive')}</Badge>, sortValue: (organization) => organization.isActive ? 'Active' : 'Inactive' },
    { id: 'created', header: t('Created'), cell: (organization) => new Date(organization.createdAt).toLocaleDateString(locale), sortValue: (organization) => new Date(organization.createdAt) },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (organization) => <RowActions label={t('Actions for {name}', { name: organization.name })} actions={canManage ? [
      { label: t('View'), icon: 'view', onSelect: () => setViewing(organization) },
      { label: t('Edit'), icon: 'edit', onSelect: () => setEditing(organization) },
      organization.isActive
        ? { label: t('Deactivate'), icon: 'archive', danger: true, onSelect: () => setChangingStatus(organization) }
        : { label: t('Reactivate'), icon: 'restore', onSelect: () => setChangingStatus(organization) },
    ] : [{ label: t('View'), icon: 'view', onSelect: () => setViewing(organization) }]} /> },
  ]
  return <>
      <PageHeader eyebrow={t('Platform administration')} title={t('Tenant management')} description={t('Provision, review, and control customer workspaces from one directory.')} actions={canManage ? <Button variant="primary" onClick={() => { setProvisioned(undefined); create.reset(); setCreating(true) }}><Plus size={14} /> {t('New tenant')}</Button> : undefined} />
      <div className="tenant-summary-grid" aria-label={t('Tenant summary')}>
        <Surface className="tenant-summary"><span><Building2 size={15} /> {t('Total tenants')}</span><strong>{organizations.data?.totalCount ?? '—'}</strong><small>{t('Provisioned workspaces')}</small></Surface>
        <Surface className="tenant-summary"><span><CheckCircle2 size={15} /> {t('Active')}</span><strong>{organizations.isSuccess ? activeCount : '—'}</strong><small>{t('Available to members')}</small></Surface>
        <Surface className="tenant-summary"><span><Clock3 size={15} /> {t('Inactive')}</span><strong>{organizations.isSuccess ? allOrganizations.length - activeCount : '—'}</strong><small>{t('Access suspended')}</small></Surface>
      </div>
      <Surface className="collection tenant-directory">
        <div className="panel-heading"><div><h2>{t('Tenant directory')}</h2><p>{t('Customer-facing workspaces remain organizations inside the domain model.')}</p></div></div>
        {organizations.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /><Skeleton /></div>
          : organizations.isError ? <EmptyState title={t('Tenant directory could not be loaded')} description={organizations.error.message} action={<Button onClick={() => organizations.refetch()}>{t('Try again')}</Button>} />
            : <DataTable labels={dataTableLabels} ariaLabel={t('Tenant directory')} data={displayedOrganizations} columns={columns} getRowId={(organization) => organization.id} searchPlaceholder={t('Search tenants…')} toolbar={statusToolbar} initialSort={{ id: 'name', direction: 'asc' }} pageSize={10} empty={<EmptyState title={t('No tenants found')} description={t('Adjust the status filter or create the first customer workspace.')} />} />}
      </Surface>
      <Dialog open={creating} onOpenChange={(open) => { setCreating(open); if (!open) setProvisioned(undefined) }} title={t(provisioned ? 'Tenant ready' : 'Create tenant')} description={t(provisioned ? 'Share the invitation with the tenant owner.' : 'Create the workspace and invite its first owner.')}>{provisioned ? <div className="token-result"><div className="delete-record-summary"><span>{t('Tenant owner')}</span><strong>{provisioned.organization.name}</strong><small>{provisioned.administratorEmail}</small></div><p>{t('The invitation expires in seven days and grants Owner access after acceptance.')}</p><code>{invitationLink(provisioned.invitationToken)}</code><Button variant="secondary" onClick={() => navigator.clipboard.writeText(invitationLink(provisioned.invitationToken))}><Copy size={14} /> {t('Copy invitation link')}</Button><div className="dialog-actions"><Button variant="primary" onClick={() => { setCreating(false); setViewing(provisioned.organization) }}>{t('Done')}</Button></div></div> : <form className="dialog-form" onSubmit={submitCreate}><label>{t('Tenant name')}<input name="name" maxLength={160} required autoFocus /></label><label>{t('Workspace handle')}<input name="slug" pattern="[a-z0-9]+(?:-[a-z0-9]+)*" maxLength={63} required placeholder="acme-operations" /><small>{t('Used internally and not shown in workspace URLs.')}</small></label><label>{t('Tenant owner')}<input name="administratorEmail" type="email" autoComplete="email" required placeholder="owner@company.com" /><small>{t('We’ll create a seven-day invitation with Owner access.')}</small></label>{create.error && <div className="form-error" role="alert">{create.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setCreating(false)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={create.isPending}>{t(create.isPending ? 'Creating…' : 'Create and invite')}</Button></div></form>}</Dialog>
      <Dialog open={editing !== undefined} onOpenChange={(open) => !open && setEditing(undefined)} title={t('Edit tenant')} description={t('Update the customer-facing workspace name. The internal handle stays stable.')}><form className="dialog-form" onSubmit={submitEdit}><label>{t('Tenant name')}<input name="name" defaultValue={editing?.name} maxLength={160} required autoFocus /></label><label>{t('Workspace handle')}<input value={editing?.slug ?? ''} disabled readOnly /><small>{t('Stable identifiers are not renamed after provisioning.')}</small></label>{update.error && <div className="form-error" role="alert">{update.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setEditing(undefined)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={update.isPending}>{t(update.isPending ? 'Saving…' : 'Save tenant')}</Button></div></form></Dialog>
      <Dialog open={changingStatus !== undefined} onOpenChange={(open) => !open && setChangingStatus(undefined)} title={t(changingStatus?.isActive ? 'Deactivate tenant' : 'Reactivate tenant')} description={t(changingStatus?.isActive ? 'Members will be unable to enter this workspace until it is reactivated.' : 'Restore member access to this workspace.')}><div className="status-confirmation"><div><span>{t('Tenant')}</span><strong>{changingStatus?.name}</strong><small>{changingStatus?.slug}</small></div>{setStatus.error && <div className="form-error" role="alert">{setStatus.error.message}</div>}<div className="dialog-actions"><Button variant="ghost" onClick={() => setChangingStatus(undefined)}>{t('Cancel')}</Button><Button variant={changingStatus?.isActive ? 'danger' : 'primary'} disabled={setStatus.isPending} onClick={() => changingStatus && setStatus.mutate(changingStatus)}>{t(setStatus.isPending ? 'Updating…' : changingStatus?.isActive ? 'Deactivate tenant' : 'Reactivate tenant')}</Button></div></div></Dialog>
      <RecordDetailsDialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.name ?? t('Tenant')} description={t('Authoritative platform tenancy and workspace context information.')} recordType={t('Tenant')} contextDescription={t('Platform operators manage the tenant lifecycle without implicit access to customer data.')} status={viewing ? <Badge tone={viewing.isActive ? 'success' : 'warning'}>{t(viewing.isActive ? 'Active' : 'Inactive')}</Badge> : undefined} details={viewing ? [{ label: t('Tenant name'), value: viewing.name }, { label: t('Workspace handle'), value: <code className="event-key">{viewing.slug}</code> }, { label: t('Tenant ID'), value: <code className="event-key">{viewing.id}</code> }, { label: t('Created'), value: formatRecordDate(viewing.createdAt) }] : []} />
  </>
}

export function PlatformOverviewPage() {
  const { locale, t } = useI18n()
  const tenants = useQuery({ queryKey: ['platform', 'tenants', 'overview'], queryFn: () => customFetch<OrganizationPage>('/api/v1/tenants?page=1&pageSize=100', { method: 'GET' }) })
  const records = tenants.data?.items ?? []
  const active = records.filter((tenant) => tenant.isActive).length
  const recent = [...records].sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()).slice(0, 5)
  return <>
    <PageHeader title={t('Platform overview')} description={t('Monitor provisioned tenant workspaces and platform health.')} actions={<Button asChild variant="primary"><Link to="/dashboard/tenants"><Plus size={14} /> {t('Add tenant')}</Link></Button>} />
    <div className="platform-metric-grid">
      <Surface><span>{t('All tenants')}</span><strong>{tenants.isLoading ? '—' : tenants.data?.totalCount ?? 0}</strong><small>{t('Across the platform')}</small><Link to="/dashboard/tenants">{t('Current workspaces')} <ArrowUpRight size={12} /></Link></Surface>
      <Surface><span>{t('Active')}</span><strong>{tenants.isLoading ? '—' : active}</strong><small>{t('In good standing')}</small><Link to="/dashboard/tenants">{t('Available to users')} <ArrowUpRight size={12} /></Link></Surface>
      <Surface><span>{t('Isolation model')}</span><strong>RLS</strong><small>{t('Shared PostgreSQL')}</small><span className="platform-metric-note">{t('Policy enforced')}</span></Surface>
      <Surface><span>{t('Access control')}</span><strong>RBAC</strong><small>{t('Organization-owned roles')}</small><Link to="/dashboard/authentication">{t('Security posture')} <ArrowUpRight size={12} /></Link></Surface>
    </div>
    <Surface className="platform-overview-panel">
      <div className="panel-title"><div><h2>{t('Tenant activity')}</h2><p>{t('Most recently provisioned workspaces.')}</p></div><Button asChild variant="ghost"><Link to="/dashboard/tenants">{t('Manage tenants')} <ArrowUpRight size={13} /></Link></Button></div>
      {tenants.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : tenants.isError ? <EmptyState title={t('Tenant activity could not be loaded')} description={tenants.error.message} /> : recent.length ? <div className="platform-tenant-list">{recent.map((tenant) => <div key={tenant.id}><span className="tenant-mark">{initials(tenant.name)}</span><span><strong>{tenant.name}</strong><small>{t('Provisioned')} {relativeTime(tenant.createdAt, locale)}</small></span><Badge tone={tenant.isActive ? 'success' : 'warning'}>{t(tenant.isActive ? 'Active' : 'Inactive')}</Badge></div>)}</div> : <EmptyState title={t('No tenants yet')} description={t('Provision the first customer workspace to begin.')} />}
    </Surface>
    <Surface className="platform-overview-panel">
      <div className="panel-title"><div><h2>{t('Platform health')}</h2><p>{t('Operational controls across tenant workspaces.')}</p></div><Badge tone="success">{t('Healthy')}</Badge></div>
      <div className="platform-health-list"><div><span>{t('Tenant provisioning')}</span><strong>{t('Ready')}</strong></div><div><span>{t('Storage isolation')}</span><strong>PostgreSQL RLS</strong></div><div><span>{t('Access control')}</span><strong>{t('Role-based')}</strong></div><div><span>{t('Audit trail')}</span><strong>{t('Enabled')}</strong></div></div>
    </Surface>
  </>
}

export function PlatformUsersPage() {
  const { locale, t } = useI18n()
  const dataTableLabels = useDataTableLabels()
  const client = useQueryClient()
  const [tab, setTab] = useState<'people' | 'invitations' | 'roles'>('people')
  const [roleFilter, setRoleFilter] = useState('all')
  const [statusFilter, setStatusFilter] = useState('all')
  const [grantOpen, setGrantOpen] = useState(false)
  const [grantResult, setGrantResult] = useState<PlatformAccessGrant>()
  const [viewing, setViewing] = useState<PlatformUser>()
  const [editing, setEditing] = useState<PlatformUser>()
  const [changingStatus, setChangingStatus] = useState<PlatformUser>()
  const [revoking, setRevoking] = useState<PlatformUser>()
  const [activation, setActivation] = useState<{ user: PlatformUser; token: string }>()
  const [editingPlatformRole, setEditingPlatformRole] = useState<PlatformRole | null | undefined>(undefined)
  const [deletingPlatformRole, setDeletingPlatformRole] = useState<PlatformRole>()
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<PlatformSession>('/api/v1/auth/session', { method: 'GET' }) })
  const directory = useQuery({ queryKey: ['platform-users'], queryFn: () => customFetch<PlatformUserPage>('/api/v1/platform-users?page=1&pageSize=100', { method: 'GET' }) })
  const roles = useQuery({ queryKey: ['platform-roles'], queryFn: () => customFetch<PlatformRole[]>('/api/v1/platform-users/roles', { method: 'GET' }) })
  const permissionModules = useQuery({ queryKey: ['platform-permissions'], queryFn: () => customFetch<PermissionModuleDto[]>('/api/v1/platform-users/permissions', { method: 'GET' }) })
  const canManage = session.data?.isPlatformAdministrator === true || session.data?.platformPermissions?.includes('platform.users.manage') === true
  const records = directory.data?.items ?? []
  const people = records.filter((user) => !user.isPendingActivation && (roleFilter === 'all' || user.roleKey === roleFilter) && (statusFilter === 'all' || (statusFilter === 'active' ? user.isActive : !user.isActive)))
  const invitations = records.filter((user) => user.isPendingActivation)

  const refresh = async () => client.invalidateQueries({ queryKey: ['platform-users'] })
  const grant = useMutation({
    mutationFn: (input: { email: string; displayName: string; roleKey: string }) => customFetch<PlatformAccessGrant>('/api/v1/platform-users', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) }),
    onSuccess: async (result) => { setGrantResult(result); await refresh() },
  })
  const changeRole = useMutation({
    mutationFn: ({ user, roleKey }: { user: PlatformUser; roleKey: string }) => customFetch<PlatformUser>(`/api/v1/platform-users/${user.id}/role`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ roleKey }) }),
    onSuccess: async () => { setEditing(undefined); await refresh() },
  })
  const changeStatus = useMutation({
    mutationFn: (user: PlatformUser) => customFetch<PlatformUser>(`/api/v1/platform-users/${user.id}/${user.isActive ? 'suspend' : 'reactivate'}`, { method: 'POST' }),
    onSuccess: async () => { setChangingStatus(undefined); await refresh() },
  })
  const revoke = useMutation({
    mutationFn: (user: PlatformUser) => customFetch<void>(`/api/v1/platform-users/${user.id}`, { method: 'DELETE' }),
    onSuccess: async () => { setRevoking(undefined); await refresh() },
  })
  const createActivation = useMutation({
    mutationFn: (user: PlatformUser) => customFetch<{ userId: string; token: string }>(`/api/v1/platform-users/${user.id}/activation-token`, { method: 'POST' }).then((result) => ({ user, ...result })),
    onSuccess: (result) => setActivation(result),
  })
  const savePlatformRole = useMutation({
    mutationFn: (input: { role?: PlatformRole | null; name: string; description: string; permissions: string[] }) => customFetch<PlatformRole>(input.role
      ? `/api/v1/platform-users/roles/${encodeURIComponent(input.role.key)}`
      : '/api/v1/platform-users/roles', { method: input.role ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: input.name, description: input.description, permissions: input.permissions }) }),
    onSuccess: async () => { setEditingPlatformRole(undefined); await client.invalidateQueries({ queryKey: ['platform-roles'] }) },
  })
  const deletePlatformRole = useMutation({
    mutationFn: (role: PlatformRole) => customFetch<void>(`/api/v1/platform-users/roles/${encodeURIComponent(role.key)}`, { method: 'DELETE' }),
    onSuccess: async () => { setDeletingPlatformRole(undefined); await client.invalidateQueries({ queryKey: ['platform-roles'] }) },
  })

  function activationLink(userId: string, token: string) {
    return `${window.location.origin}/activate-access?userId=${encodeURIComponent(userId)}&token=${encodeURIComponent(token)}`
  }
  function submitGrant(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    grant.mutate({ email: String(data.get('email')), displayName: String(data.get('displayName')), roleKey: String(data.get('roleKey')) })
  }
  function submitRole(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!editing) return
    const data = new FormData(event.currentTarget)
    changeRole.mutate({ user: editing, roleKey: String(data.get('roleKey')) })
  }
  function actionsFor(user: PlatformUser): RowAction[] {
    const actions: RowAction[] = [{ label: t('View'), icon: 'view', onSelect: () => setViewing(user) }]
    if (!canManage || user.id === session.data?.userId) return actions
    if (user.isPendingActivation) {
      actions.push({ label: t('Activation link'), icon: 'view', onSelect: () => createActivation.mutate(user) })
    } else {
      actions.push(
        { label: t('Change role'), icon: 'edit', onSelect: () => setEditing(user) },
        user.isActive
          ? { label: t('Suspend access'), icon: 'archive', onSelect: () => setChangingStatus(user) }
          : { label: t('Reactivate access'), icon: 'restore', onSelect: () => setChangingStatus(user) },
      )
    }
    actions.push({ label: t('Remove access'), icon: 'delete', danger: true, onSelect: () => setRevoking(user) })
    return actions
  }
  function roleActions(role: PlatformRole): RowAction[] {
    if (!canManage || role.isSystem || !role.canAssign) return []
    return [
      { label: t('Edit'), icon: 'edit', onSelect: () => setEditingPlatformRole(role) },
      { label: t('Delete'), icon: 'delete', danger: true, onSelect: () => setDeletingPlatformRole(role) },
    ]
  }
  const personColumns: DataTableColumn<PlatformUser>[] = [
    { id: 'person', header: t('Person'), hideable: false, cell: (user) => <div className="user-cell"><span className="user-avatar">{initials(user.displayName || user.email)}</span><span><strong>{user.displayName}</strong><small>{user.email}</small></span></div>, sortValue: (user) => user.displayName, searchValue: (user) => `${user.displayName} ${user.email}` },
    { id: 'role', header: t('Platform role'), cell: (user) => <Badge tone={user.roleKey === 'platform-administrator' ? 'info' : 'neutral'}>{t(user.roleName)}</Badge>, sortValue: (user) => user.roleName },
    { id: 'status', header: t('Status'), cell: (user) => <Badge tone={user.isActive ? 'success' : 'warning'}>{t(user.isActive ? 'Active' : 'Suspended')}</Badge>, sortValue: (user) => user.isActive ? 'Active' : 'Suspended' },
    { id: 'lastActive', header: t('Last sign-in'), cell: (user) => user.lastSignedInAt ? relativeTime(user.lastSignedInAt, locale) : t('Never'), sortValue: (user) => user.lastSignedInAt ? new Date(user.lastSignedInAt) : null },
    { id: 'added', header: t('Added'), defaultVisible: false, cell: (user) => new Date(user.createdAt).toLocaleDateString(locale), sortValue: (user) => new Date(user.createdAt) },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (user) => <RowActions label={t('Actions for {name}', { name: user.displayName })} actions={actionsFor(user)} /> },
  ]
  const invitationColumns: DataTableColumn<PlatformUser>[] = [
    { id: 'person', header: t('Invited person'), hideable: false, cell: (user) => <div className="user-cell"><span className="user-avatar">{initials(user.displayName || user.email)}</span><span><strong>{user.displayName}</strong><small>{user.email}</small></span></div>, sortValue: (user) => user.displayName, searchValue: (user) => `${user.displayName} ${user.email}` },
    { id: 'role', header: t('Access on activation'), cell: (user) => <Badge>{t(user.roleName)}</Badge>, sortValue: (user) => user.roleName },
    { id: 'status', header: t('Status'), cell: () => <Badge tone="warning">{t('Pending activation')}</Badge>, sortValue: () => 'Pending' },
    { id: 'invited', header: t('Invited'), cell: (user) => new Date(user.createdAt).toLocaleDateString(locale), sortValue: (user) => new Date(user.createdAt) },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (user) => <RowActions label={t('Actions for invitation to {email}', { email: user.email })} actions={actionsFor(user)} /> },
  ]
  const roleColumns: DataTableColumn<PlatformRole>[] = [
    { id: 'name', header: t('Role'), hideable: false, cell: (role) => <div className="role-name-cell"><strong>{role.isSystem ? t(role.name) : role.name}</strong><small>{role.isSystem ? t('Built-in role') : role.description}</small></div>, sortValue: (role) => role.order, searchValue: (role) => `${role.name} ${role.description} ${role.permissions.join(' ')}` },
    { id: 'permissions', header: t('Permissions'), cell: (role) => <div className="role-grant-summary"><strong>{role.permissions.length}</strong><small>{t(role.permissions.length === 1 ? 'permission' : 'permissions')}</small></div>, sortValue: (role) => role.permissions.length },
    { id: 'actions', header: '', hideable: false, align: 'right', width: 54, cell: (role) => roleActions(role).length > 0 ? <RowActions label={t('Actions for {name}', { name: role.name })} actions={roleActions(role)} /> : null },
  ]
  const filters = <><select aria-label={t('Filter platform users by role')} value={roleFilter} onChange={(event) => setRoleFilter(event.target.value)}><option value="all">{t('All roles')}</option>{roles.data?.map((role) => <option key={role.key} value={role.key}>{role.name}</option>)}</select><select aria-label={t('Filter platform users by status')} value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}><option value="all">{t('All statuses')}</option><option value="active">{t('Active')}</option><option value="suspended">{t('Suspended')}</option></select></>
  const error = directory.error ?? roles.error ?? permissionModules.error ?? grant.error ?? changeRole.error ?? changeStatus.error ?? revoke.error ?? createActivation.error ?? savePlatformRole.error ?? deletePlatformRole.error
  const headerAction = canManage && tab === 'roles'
    ? <Button variant="primary" onClick={() => { savePlatformRole.reset(); setEditingPlatformRole(null) }}><Plus size={14} /> {t('New role')}</Button>
    : canManage ? <Button variant="primary" onClick={() => { setGrantResult(undefined); grant.reset(); setGrantOpen(true) }}><UserPlus size={14} /> {t('Add platform user')}</Button> : undefined
  return <>
    <PageHeader eyebrow={t('Platform access')} title={t('User management')} description={t('Manage global platform operators independently from tenant workspace membership.')} actions={headerAction} />
    <div className="user-management-tabs" role="tablist" aria-label={t('User management')}>
      <button role="tab" aria-selected={tab === 'people'} className={tab === 'people' ? 'active' : ''} onClick={() => setTab('people')}><Users size={16} /> {t('People')} <Badge>{records.filter((user) => !user.isPendingActivation).length}</Badge></button>
      <button role="tab" aria-selected={tab === 'invitations'} className={tab === 'invitations' ? 'active' : ''} onClick={() => setTab('invitations')}><Mail size={16} /> {t('Invitations')} <Badge>{invitations.length}</Badge></button>
      <button role="tab" aria-selected={tab === 'roles'} className={tab === 'roles' ? 'active' : ''} onClick={() => setTab('roles')}><ShieldCheck size={16} /> {t('Roles')} <Badge>{roles.data?.length ?? 0}</Badge></button>
    </div>
    {error && <div className="page-alert" role="alert">{error.message}</div>}
    {tab === 'people' ? <Surface className="collection"><div className="panel-heading"><div><h2>{t('Platform directory')}</h2><p>{t('People authorized to operate or review the platform.')}</p></div></div>{directory.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : <DataTable labels={dataTableLabels} ariaLabel={t('Platform users')} data={people} columns={personColumns} getRowId={(user) => user.id} searchPlaceholder={t('Search platform users…')} toolbar={filters} initialSort={{ id: 'person', direction: 'asc' }} pageSize={20} empty={<EmptyState title={t('No platform users found')} description={t('Adjust the filters or add a platform user.')} />}/>}</Surface>
      : tab === 'invitations' ? <Surface className="collection"><div className="panel-heading"><div><h2>{t('Pending invitations')}</h2><p>{t('Secure activation links for new platform identities.')}</p></div></div>{directory.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : <DataTable labels={dataTableLabels} ariaLabel={t('Platform invitations')} data={invitations} columns={invitationColumns} getRowId={(user) => user.id} searchPlaceholder={t('Search invitations…')} initialSort={{ id: 'invited', direction: 'desc' }} pageSize={20} empty={<EmptyState title={t('No pending invitations')} description={t('New platform invitations appear here until the recipient activates their account.')} />}/>}</Surface>
        : <Surface className="collection"><div className="panel-heading"><div><h2>{t('Platform roles')}</h2><p>{t('Expand a role to review what it can do.')}</p></div></div>{roles.isLoading ? <div className="skeleton-list"><Skeleton /><Skeleton /></div> : <DataTable labels={dataTableLabels} ariaLabel={t('Platform roles')} data={roles.data ?? []} columns={roleColumns} getRowId={(role) => role.key} searchPlaceholder={t('Search roles or permissions…')} initialSort={{ id: 'name', direction: 'asc' }} getRowExpansionLabel={(role) => role.name} renderExpandedRow={(role) => <div className="platform-role-permissions"><strong>{role.name} {t('capabilities')}</strong><ul>{platformCapabilities(role.permissions).map((capability) => <li key={capability.name}><span>{t(capability.name)}</span><Badge tone={capability.access === 'Manage' ? 'info' : 'neutral'}>{t(capability.access)}</Badge></li>)}</ul></div>} empty={<EmptyState title={t('No platform roles')} description={t('Built-in platform roles could not be loaded.')} />}/>}</Surface>}

    <Dialog open={grantOpen} onOpenChange={(open) => { setGrantOpen(open); if (!open) setGrantResult(undefined) }} title={t('Add platform user')} description={t('Invite someone to help manage the platform.')}>{grantResult ? <div className="token-result"><div className="delete-record-summary"><span>{t('Platform access')}</span><strong>{grantResult.user.displayName}</strong><small>{grantResult.user.roleName} · {grantResult.user.email}</small></div>{grantResult.activationToken ? <><p>{t('Share this 24-hour activation link through a secure channel. It stops working after the account is activated.')}</p><code>{activationLink(grantResult.user.id, grantResult.activationToken)}</code><Button onClick={() => navigator.clipboard.writeText(activationLink(grantResult.user.id, grantResult.activationToken!))}><Copy size={14} /> {t('Copy activation link')}</Button></> : <p>{t('The existing verified identity can use its current sign-in credentials immediately.')}</p>}<div className="dialog-actions"><Button variant="primary" onClick={() => setGrantOpen(false)}>{t('Done')}</Button></div></div> : <form className="dialog-form platform-access-form" onSubmit={submitGrant}><div className="platform-identity-fields"><label>{t('Display name')}<input name="displayName" minLength={2} maxLength={120} required autoFocus /></label><label>{t('Email address')}<input name="email" type="email" autoComplete="email" required /></label></div><PlatformRolePicker roles={roles.data ?? []} defaultValue="platform-operator" /><p className="platform-access-note">{t('Tenant access is managed separately.')}</p>{grant.error && <div className="form-error" role="alert">{grant.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setGrantOpen(false)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={grant.isPending || roles.isLoading}>{t(grant.isPending ? 'Adding…' : 'Add platform user')}</Button></div></form>}</Dialog>
    <Dialog open={editing !== undefined} onOpenChange={(open) => !open && setEditing(undefined)} title={t('Change platform role')} description={t('Apply a least-privilege platform access profile.')}>{editing && <form className="dialog-form platform-access-form" onSubmit={submitRole}><div className="delete-record-summary"><span>{t('Platform user')}</span><strong>{editing.displayName}</strong><small>{editing.email}</small></div><PlatformRolePicker roles={roles.data ?? []} defaultValue={editing.roleKey} />{changeRole.error && <div className="form-error" role="alert">{changeRole.error.message}</div>}<div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setEditing(undefined)}>{t('Cancel')}</Button><Button type="submit" variant="primary" disabled={changeRole.isPending}>{t('Save access')}</Button></div></form>}</Dialog>
    <Dialog open={changingStatus !== undefined} onOpenChange={(open) => !open && setChangingStatus(undefined)} title={t(changingStatus?.isActive ? 'Suspend platform access' : 'Reactivate platform access')} description={t(changingStatus?.isActive ? 'The user will be signed out and unable to use platform administration.' : 'Restore this user’s assigned platform role.')}><div className="status-confirmation"><div><span>{t('Platform user')}</span><strong>{changingStatus?.displayName}</strong><small>{changingStatus?.roleName} · {changingStatus?.email}</small></div>{changeStatus.error && <div className="form-error" role="alert">{changeStatus.error.message}</div>}<div className="dialog-actions"><Button variant="ghost" onClick={() => setChangingStatus(undefined)}>{t('Cancel')}</Button><Button variant={changingStatus?.isActive ? 'danger' : 'primary'} disabled={changeStatus.isPending} onClick={() => changingStatus && changeStatus.mutate(changingStatus)}>{t(changeStatus.isPending ? 'Updating…' : changingStatus?.isActive ? 'Suspend access' : 'Reactivate access')}</Button></div></div></Dialog>
    <Dialog open={revoking !== undefined} onOpenChange={(open) => !open && setRevoking(undefined)} title={t('Remove platform access')} description={t('Remove platform authorization without deleting the person’s global identity or tenant memberships.')}><div className="status-confirmation"><div><span>{t('Platform user')}</span><strong>{revoking?.displayName}</strong><small>{revoking?.roleName} · {revoking?.email}</small></div><p className="delete-accountability">{t('This person will be signed out of platform administration. Their account and tenant access remain unchanged.')}</p>{revoke.error && <div className="form-error" role="alert">{revoke.error.message}</div>}<div className="dialog-actions"><Button variant="ghost" onClick={() => setRevoking(undefined)}>{t('Cancel')}</Button><Button variant="danger" disabled={revoke.isPending} onClick={() => revoking && revoke.mutate(revoking)}>{t(revoke.isPending ? 'Removing…' : 'Remove access')}</Button></div></div></Dialog>
    <Dialog open={activation !== undefined} onOpenChange={(open) => !open && setActivation(undefined)} title={t('Platform activation link')} description={t('Share this link securely with the invited person.')}>{activation && <div className="token-result"><div className="delete-record-summary"><span>{t('Recipient')}</span><strong>{activation.user.displayName}</strong><small>{activation.user.email}</small></div><code>{activationLink(activation.user.id, activation.token)}</code><Button onClick={() => navigator.clipboard.writeText(activationLink(activation.user.id, activation.token))}><Copy size={14} /> {t('Copy activation link')}</Button></div>}</Dialog>
    <RoleEditorDialog open={editingPlatformRole !== undefined} role={editingPlatformRole ? { name: editingPlatformRole.name, description: editingPlatformRole.description, permissions: editingPlatformRole.permissions } : editingPlatformRole} modules={permissionModules.data ?? []} isLoading={permissionModules.isLoading} isSaving={savePlatformRole.isPending} error={savePlatformRole.error?.message} onOpenChange={(open) => { if (!open) { setEditingPlatformRole(undefined); savePlatformRole.reset() } }} onSave={(value) => savePlatformRole.mutate({ role: editingPlatformRole, ...value })} />
    <Dialog open={deletingPlatformRole !== undefined} onOpenChange={(open) => !open && setDeletingPlatformRole(undefined)} title={t('Delete custom role')} description={t('Remove this unassigned platform role permanently.')}><div className="status-confirmation"><div><span>{t('Custom platform role')}</span><strong>{deletingPlatformRole?.name}</strong><small>{t('Built-in roles remain protected.')}</small></div><p className="delete-accountability">{t('This action cannot be undone. Assignments must be moved to another role before deletion.')}</p>{deletePlatformRole.error && <div className="form-error" role="alert">{deletePlatformRole.error.message}</div>}<div className="dialog-actions"><Button variant="ghost" onClick={() => setDeletingPlatformRole(undefined)}>{t('Cancel')}</Button><Button variant="danger" disabled={deletePlatformRole.isPending} onClick={() => deletingPlatformRole && deletePlatformRole.mutate(deletingPlatformRole)}>{t(deletePlatformRole.isPending ? 'Deleting…' : 'Delete role')}</Button></div></div></Dialog>
    <RecordDetailsDialog open={viewing !== undefined} onOpenChange={(open) => !open && setViewing(undefined)} title={viewing?.displayName ?? t('Platform user')} description={t('Global identity and platform authorization details.')} recordType={t('Platform user')} status={viewing ? <Badge tone={viewing.isPendingActivation ? 'warning' : viewing.isActive ? 'success' : 'warning'}>{t(viewing.isPendingActivation ? 'Pending activation' : viewing.isActive ? 'Active' : 'Suspended')}</Badge> : undefined} details={viewing ? [{ label: t('Display name'), value: viewing.displayName }, { label: t('Email'), value: viewing.email }, { label: t('Platform role'), value: viewing.roleName }, { label: t('Created'), value: formatRecordDate(viewing.createdAt) }, { label: t('Last sign-in'), value: viewing.lastSignedInAt ? formatRecordDate(viewing.lastSignedInAt) : t('Never') }, { label: t('Identity ID'), value: <code className="event-key">{viewing.id}</code> }] : []} />
  </>
}

export function PlatformAccessActivationPage() {
  const { t } = useI18n()
  const query = new URLSearchParams(window.location.search)
  const userId = query.get('userId') ?? ''
  const token = query.get('token') ?? ''
  const [complete, setComplete] = useState(false)
  const [validationError, setValidationError] = useState<string>()
  const activate = useMutation({ mutationFn: (password: string) => customFetch<void>('/api/v1/access-activation', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ userId, token, password }) }), onSuccess: () => setComplete(true) })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const password = String(data.get('password'))
    if (password !== String(data.get('confirmation'))) { setValidationError(t('Passwords do not match.')); return }
    setValidationError(undefined)
    activate.mutate(password)
  }
  return <main className="auth-page"><div className="auth-language"><LanguageSwitcher compact /></div><div className="auth-brand"><span className="brand-mark"><TrykatchLogo size={17} /></span><strong>Trykatch</strong></div><Surface className="auth-card"><div className="eyebrow">{t('Platform access')}</div><h1>{t(complete ? 'Account activated' : 'Activate your account')}</h1><p>{t(complete ? 'Your platform identity is ready. Sign in to continue.' : 'Choose a strong password to accept your platform invitation.')}</p>{complete ? <Button asChild variant="primary"><Link to="/login">{t('Continue to sign in')}</Link></Button> : userId && token ? <form onSubmit={submit}><PasswordField label={t('New password')} name="password" autoComplete="new-password" minLength={12} visibilityLabel={t('New password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required autoFocus /><PasswordField label={t('Confirm password')} name="confirmation" autoComplete="new-password" minLength={12} visibilityLabel={t('Confirm password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />{(validationError || activate.error) && <div className="form-error" role="alert">{validationError ?? activate.error?.message}</div>}<Button type="submit" variant="primary" disabled={activate.isPending}>{t(activate.isPending ? 'Activating…' : 'Activate account')}</Button></form> : <div className="form-error" role="alert">{t('This activation link is incomplete.')}</div>}</Surface></main>
}

export function PlatformInvitationsPage() {
  const { t } = useI18n()
  return <><PageHeader title={t('Invitations')} description={t('Monitor workspace invitations without exposing tenant context in application URLs.')} /><Surface className="platform-overview-panel"><div className="panel-title"><div><h2>{t('Invitation operations')}</h2><p>{t('Invitation ownership and acceptance remain scoped to the destination workspace.')}</p></div><Badge>{t('Audited')}</Badge></div><EmptyState title={t('No platform alerts')} description={t('Pending, expired, and revoked invitation reporting can be added here without weakening organization isolation.')} /></Surface></>
}

export function PlatformAuthenticationPage() {
  const { t } = useI18n()
  return <><PageHeader title={t('Authentication')} description={t('Platform-wide sign-in policy and protocol posture.')} /><div className="platform-auth-grid"><Surface><ShieldCheck size={18} /><div><strong>{t('First-party sessions')}</strong><small>{t('HttpOnly cookies, antiforgery, and server-side workspace context')}</small></div><Badge tone="success">{t('Protected')}</Badge></Surface><Surface><KeyRound size={18} /><div><strong>OpenID Connect</strong><small>{t('Authorization Code + PKCE and client credentials')}</small></div><Badge tone="success">{t('Enabled')}</Badge></Surface><Surface><Activity size={18} /><div><strong>{t('Risk controls')}</strong><small>{t('Lockout, MFA, rate limiting, and security-stamp invalidation')}</small></div><Badge tone="success">{t('Active')}</Badge></Surface></div></>
}

export function AcceptInvitationPage() {
  const { t, formatDate } = useI18n()
  const { token } = useParams({ from: '/invite/$token' })
  const [validationError, setValidationError] = useState<string>()
  const preview = useQuery({ queryKey: ['invitation-preview', token], queryFn: () => customFetch<{ email: string; organizationName: string; expiresAt: string; accountExists: boolean }>(`/api/v1/invitations/preview/${encodeURIComponent(token)}`, { method: 'GET' }), retry: false })
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<PlatformSession>('/api/v1/auth/session', { method: 'GET' }), retry: false })
  const accept = useMutation({
    mutationFn: () => customFetch<{ organizationId: string }>('/api/v1/invitations/accept', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token }) }),
    onSuccess: () => window.location.assign('/overview'),
  })
  const activate = useMutation({
    mutationFn: (input: { firstName: string; lastName: string; password: string }) => customFetch<{ organizationId: string }>('/api/v1/invitations/activate', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token, ...input }) }),
    onSuccess: () => window.location.assign('/overview'),
  })
  function submitActivation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const password = String(data.get('password'))
    if (password !== String(data.get('confirmation'))) { setValidationError(t('Passwords do not match.')); return }
    setValidationError(undefined)
    activate.mutate({ firstName: String(data.get('firstName')), lastName: String(data.get('lastName')), password })
  }
  const signedInAsInvitee = Boolean(preview.data?.email && session.data?.email && preview.data.email.toLowerCase() === session.data.email.toLowerCase())
  const returnTo = encodeURIComponent(`/invite/${token}`)
  const invitation = preview.data
  return <main className="auth-page"><div className="auth-language"><LanguageSwitcher compact /></div><div className="auth-brand"><span className="brand-mark"><TrykatchLogo size={17} /></span><strong>Trykatch</strong></div><Surface className="auth-card"><div className="eyebrow">{t('Workspace invitation')}</div>{preview.isLoading ? <><h1>{t('Checking invitation…')}</h1><Skeleton /></> : preview.isError || !invitation ? <><h1>{t('Invitation unavailable')}</h1><div className="form-error" role="alert">{preview.error?.message ?? t('This invitation could not be loaded.')}</div></> : <><h1>{t('Join')} {invitation.organizationName}</h1><p>{t('You were invited as')} <strong>{invitation.email}</strong>.</p>{invitation.accountExists ? signedInAsInvitee ? <>{accept.error && <div className="form-error" role="alert">{accept.error.message}</div>}<Button variant="primary" onClick={() => accept.mutate()} disabled={accept.isPending}>{t(accept.isPending ? 'Joining…' : 'Join workspace')}</Button></> : <><p>{t('Sign in with the invited email address to continue.')}</p><Button asChild variant="primary"><a href={`/login?returnTo=${returnTo}`}>{t('Sign in to accept')}</a></Button></> : <form onSubmit={submitActivation}><div className="invitation-name-fields"><label>{t('First name')}<input name="firstName" autoComplete="given-name" minLength={1} maxLength={60} required autoFocus /></label><label>{t('Last name')}<input name="lastName" autoComplete="family-name" minLength={1} maxLength={60} required /></label></div><PasswordField label={t('Create password')} name="password" autoComplete="new-password" minLength={12} visibilityLabel={t('Password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required /><PasswordField label={t('Confirm password')} name="confirmation" autoComplete="new-password" minLength={12} visibilityLabel={t('Confirm password').toLowerCase()} showLabel={t('Show')} hideLabel={t('Hide')} required />{(validationError || activate.error) && <div className="form-error" role="alert">{validationError ?? activate.error?.message}</div>}<Button type="submit" variant="primary" disabled={activate.isPending}>{t(activate.isPending ? 'Creating account…' : 'Create account and join')}</Button></form>}</>}</Surface>{invitation && <small className="auth-note">{t('Invitation expires')} {formatDate(invitation.expiresAt)}</small>}</main>
}
