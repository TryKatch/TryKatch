import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { customFetch } from '@flatpackapp/api-client'
import type { FlatpackNavigationContribution } from '@flatpackapp/module-sdk'
import { Button, Dialog, Skeleton } from '@flatpackapp/ui'
import {
  Activity,
  ArchiveRestore,
  ChevronDown,
  LayoutDashboard,
  LogOut,
  Menu,
  Monitor,
  Moon,
  Palette,
  PanelLeftClose,
  PanelLeftOpen,
  Search,
  Sun,
  UserRound,
  Users,
  X,
  WifiOff,
} from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { FlatpackLogo } from '../components/FlatpackLogo'
import { SignOutDialog } from '../components/SignOutDialog'
import { workspaceModules } from '../modules'
import { applyAppearance, defaultShellColor, type Theme } from './appearance'

const coreNavigation: readonly FlatpackNavigationContribution[] = [
  { id: 'core.overview', section: 'Workspace', order: 10, to: '/overview', label: 'Overview', icon: LayoutDashboard, exact: true },
  { id: 'core.user-management', section: 'Administration', order: 10, to: '/user-management', label: 'User Management', icon: Users },
  { id: 'core.audit', section: 'Administration', order: 20, to: '/audit', label: 'Audit', icon: Activity },
  { id: 'core.archive', section: 'Recovery', order: 10, to: '/archive', label: 'Archive', icon: ArchiveRestore },
]
const allNavigation = [...coreNavigation, ...workspaceModules.navigationFor('workspace')]
  .toSorted((left, right) => left.order - right.order || left.id.localeCompare(right.id))
const sectionOrder = ['Workspace', 'Administration', 'Recovery']

interface Session { displayName: string; email: string; isPlatformAdministrator: boolean }
interface OrganizationAccess { permissions: string[] }
interface RequestError extends Error { status?: number }

function getInitials(value: string) {
  return value.split(/\s+/).filter(Boolean).map((part) => part[0]).join('').slice(0, 2).toUpperCase() || 'U'
}

export function AppShell() {
  const pathname = useRouterState({ select: (state) => state.location.pathname })
  const queryClient = useQueryClient()
  const accountMenu = useRef<HTMLDivElement>(null)
  const [commandOpen, setCommandOpen] = useState(false)
  const [accountOpen, setAccountOpen] = useState(false)
  const [signOutOpen, setSignOutOpen] = useState(false)
  const [appearanceOpen, setAppearanceOpen] = useState(false)
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const [collapsed, setCollapsed] = useState(() => {
    const stored = localStorage.getItem('flatpack-sidebar')
    return stored ? stored === 'collapsed' : matchMedia('(max-width: 900px)').matches
  })
  const [theme, setTheme] = useState<Theme>(() => {
    const stored = localStorage.getItem('flatpack-theme')
    return stored === 'light' || stored === 'dark' || stored === 'custom' || stored === 'system' ? stored : 'system'
  })
  const [shellColor, setShellColor] = useState(() => {
    const stored = localStorage.getItem('flatpack-shell-color')
    return stored && /^#[0-9a-f]{6}$/i.test(stored) ? stored : defaultShellColor
  })
  const session = useQuery({
    queryKey: ['me', 'session'],
    queryFn: () => customFetch<Session>('/api/v1/auth/session', { method: 'GET' }),
    retry: (failureCount, error) => (error as RequestError).status !== 401 && failureCount < 1,
    retryDelay: (attempt) => Math.min(750 * 2 ** attempt, 5_000),
    refetchInterval: (query) => query.state.status === 'error' ? 3_000 : false,
    refetchOnWindowFocus: true,
  })
  const access = useQuery({
    queryKey: ['access'],
    queryFn: () => customFetch<OrganizationAccess>('/api/v1/access', { method: 'GET' }),
    enabled: Boolean(session.data),
  })
  const logout = useMutation({
    mutationFn: () => customFetch<void>('/api/v1/auth/logout', { method: 'POST' }),
    onSuccess: () => {
      queryClient.clear()
      window.location.assign('/login')
    },
  })

  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)')
    const apply = () => applyAppearance(document.documentElement, theme, shellColor, media.matches)
    apply()
    media.addEventListener('change', apply)
    localStorage.setItem('flatpack-theme', theme)
    return () => media.removeEventListener('change', apply)
  }, [shellColor, theme])

  useEffect(() => {
    localStorage.setItem('flatpack-shell-color', shellColor)
    localStorage.removeItem('flatpack-accent')
  }, [shellColor])

  useEffect(() => {
    localStorage.setItem('flatpack-sidebar', collapsed ? 'collapsed' : 'expanded')
  }, [collapsed])

  useEffect(() => {
    if ((session.error as RequestError | null)?.status === 401) window.location.replace('/login')
  }, [session.error])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        setCommandOpen((open) => !open)
      }
      if (event.key === 'Escape') {
        setAccountOpen(false)
        setMobileNavOpen(false)
      }
    }
    const onPointerDown = (event: PointerEvent) => {
      if (accountMenu.current && !accountMenu.current.contains(event.target as Node)) setAccountOpen(false)
    }
    window.addEventListener('keydown', onKeyDown)
    window.addEventListener('pointerdown', onPointerDown)
    return () => {
      window.removeEventListener('keydown', onKeyDown)
      window.removeEventListener('pointerdown', onPointerDown)
    }
  }, [])

  const identity = session.data?.displayName || session.data?.email || 'Account'
  const initials = getInitials(identity)
  const area = pathname.endsWith('/archive') ? 'Recovery' : pathname.includes('/user-management') || pathname.endsWith('/audit') ? 'Administration' : 'Workspace'
  const permittedNavigation = allNavigation.filter((item) => !item.requiredPermission || access.data?.permissions.includes(item.requiredPermission))
  const navSections = sectionOrder.map((label) => ({ label, items: permittedNavigation.filter((item) => item.section === label) }))

  return <div className={`app-shell${collapsed ? ' is-collapsed' : ''}${mobileNavOpen ? ' is-mobile-nav-open' : ''}`}>
    <aside className="sidebar" id="organization-navigation">
      <div className="mobile-sidebar-heading">
        <Link className="brand" to="/overview" aria-label="Flatpack overview" onClick={() => setMobileNavOpen(false)}><span className="brand-mark"><FlatpackLogo size={17} /></span><span className="sidebar-label">Flatpack</span></Link>
        <button className="mobile-nav-close" type="button" aria-label="Close navigation" onClick={() => setMobileNavOpen(false)}><X size={19} /></button>
      </div>
      <nav aria-label="Organization navigation">
        {navSections.map((section) => <section className="sidebar-nav-section" key={section.label} aria-labelledby={`nav-${section.label.toLowerCase()}`}>
          <span className="sidebar-label sidebar-section-label" id={`nav-${section.label.toLowerCase()}`}>{section.label}</span>
          <div>{section.items.map(({ id, to, label, icon: Icon, exact }) => <Link key={id} to={to} aria-label={label} title={label} activeOptions={{ exact }} activeProps={{ className: 'active' }} onClick={() => setMobileNavOpen(false)}><Icon size={16} /><span className="sidebar-label">{label}</span></Link>)}</div>
        </section>)}
      </nav>
      <div className="sidebar-bottom" ref={accountMenu}>
        <div className="account-menu" data-open={accountOpen} role="dialog" aria-label="Account and appearance" aria-hidden={!accountOpen} inert={!accountOpen ? true : undefined}>
          <div className="account-menu-identity"><strong>{identity}</strong><span>{session.data?.email}</span></div>
          <Link className="account-menu-item" to="/profile" onClick={() => setAccountOpen(false)}><UserRound size={17} /> Profile</Link>
          <button className="account-menu-item" type="button" aria-expanded={appearanceOpen} onClick={() => setAppearanceOpen((open) => !open)}><Palette size={17} /> Appearance <ChevronDown className={`menu-chevron${appearanceOpen ? ' is-open' : ''}`} size={15} /></button>
          <div className="appearance-collapse" data-open={appearanceOpen} aria-hidden={!appearanceOpen} inert={!appearanceOpen ? true : undefined}><div>
            <div className="appearance-panel">
              <span className="appearance-label">Theme</span>
              {([['light', Sun], ['system', Monitor], ['dark', Moon], ['custom', Palette]] as const).map(([value, Icon]) => <button key={value} type="button" aria-label={`${value[0].toUpperCase() + value.slice(1)} theme`} className={theme === value ? 'selected' : ''} onClick={() => setTheme(value)}><Icon size={15} /> <span>{value[0].toUpperCase() + value.slice(1)}{value === 'custom' && <small>{shellColor.toUpperCase()}</small>}</span><span aria-hidden="true">{theme === value ? '✓' : ''}</span></button>)}
              {theme === 'custom' && <label className="shell-color-control">
                <span className="shell-color-well" style={{ backgroundColor: shellColor }}><input type="color" value={shellColor} aria-label="Custom shell color" onChange={(event) => { setShellColor(event.target.value); setTheme('custom') }} /></span>
                <span><strong>Shell color</strong><small>Choose any base color</small></span>
              </label>}
            </div>
          </div></div>
          <div className="account-menu-separator" />
          <button className="account-menu-item danger-text" type="button" onClick={() => { setAccountOpen(false); setSignOutOpen(true) }}><LogOut size={17} /> Log out</button>
        </div>
        <div className="sidebar-controls">
          <button className="account-trigger" type="button" disabled={!session.data} aria-label={`Account menu for ${identity}`} aria-expanded={accountOpen} onClick={() => setAccountOpen((open) => !open)}><span className="avatar">{initials}</span><span className="sidebar-label account-trigger-name">{identity}</span><ChevronDown className={`sidebar-label account-trigger-chevron${accountOpen ? ' is-open' : ''}`} size={14} /></button>
          <button className="collapse-trigger" type="button" aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} onClick={() => { setAccountOpen(false); setCollapsed((value) => !value) }}>{collapsed ? <PanelLeftOpen size={19} /> : <PanelLeftClose size={19} />}</button>
        </div>
      </div>
    </aside>
    <button className="mobile-nav-backdrop" type="button" aria-label="Close navigation" tabIndex={mobileNavOpen ? 0 : -1} onClick={() => { setMobileNavOpen(false); setAccountOpen(false) }} />
    <main className="workspace">
      <header className="topbar"><button className="mobile-nav-trigger" type="button" aria-label="Open navigation" aria-controls="organization-navigation" aria-expanded={mobileNavOpen} onClick={() => setMobileNavOpen(true)}><Menu size={19} /></button><button className="command" onClick={() => setCommandOpen(true)} disabled={!session.data}><Search size={14} /><span>Search or jump to…</span><kbd>⌘ K</kbd></button><div className="top-actions"><span className={`session-status${session.isError ? ' is-offline' : ''}`}><span /> {session.isError ? 'Reconnecting' : session.isPending ? 'Connecting' : 'Session protected'}</span></div></header>
      <div className="secondary-nav"><strong>{area}</strong></div>
      <section className="page">
        {session.isPending ? <div className="shell-loading" aria-label="Loading account"><Skeleton /><Skeleton /><Skeleton /></div>
          : session.isError ? <div className="shell-state" role="alert"><span><WifiOff size={21} /></span><h1>Connection interrupted</h1><p>The application API is temporarily unavailable. Flatpack will keep trying to reconnect.</p><Button variant="secondary" onClick={() => session.refetch()} disabled={session.isFetching}>{session.isFetching ? 'Reconnecting…' : 'Try again'}</Button></div>
            : <Outlet />}
      </section>
    </main>
    <Dialog open={commandOpen} onOpenChange={setCommandOpen} title="Jump to" description="Navigate this organization without leaving the keyboard.">
      <nav className="command-list" aria-label="Command palette">
        {permittedNavigation.map(({ id, to, label, icon: Icon }) => <Link key={id} to={to} onClick={() => setCommandOpen(false)}><Icon size={15} /><span>{label}</span></Link>)}
      </nav>
    </Dialog>
    <SignOutDialog open={signOutOpen} identity={identity} isPending={logout.isPending} error={logout.error?.message} onOpenChange={setSignOutOpen} onConfirm={() => logout.mutate()} />
  </div>
}
