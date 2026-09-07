import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { customFetch } from '@flatpackapp/api-client'
import { Button, Skeleton } from '@flatpackapp/ui'
import { Bell, Building2, ChevronDown, LayoutDashboard, LogOut, Menu, Monitor, Moon, Palette, PanelLeftClose, PanelLeftOpen, ShieldCheck, Sun, UserRound, Users, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { FlatpackLogo } from '../components/FlatpackLogo'
import { applyAppearance, defaultShellColor, type Theme } from './appearance'

interface Session { displayName: string; email: string; hasPlatformAccess?: boolean; isPlatformAdministrator?: boolean; platformPermissions?: string[] }

const platformNavigation = [
  { to: '/dashboard', label: 'Platform overview', icon: LayoutDashboard, permission: 'platform.dashboard.read', exact: true },
  { to: '/dashboard/tenants', label: 'Tenant management', icon: Building2, permission: 'platform.tenants.read' },
  { to: '/dashboard/users', label: 'User management', icon: Users, permission: 'platform.users.read' },
  { to: '/dashboard/authentication', label: 'Authentication', icon: ShieldCheck, permission: 'platform.authentication.read' },
] as const

function initials(value: string) {
  return value.split(/\s+/).filter(Boolean).map((part) => part[0]).join('').slice(0, 2).toUpperCase() || 'U'
}

export function PlatformShell() {
  const pathname = useRouterState({ select: (state) => state.location.pathname })
  const queryClient = useQueryClient()
  const accountMenu = useRef<HTMLDivElement>(null)
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('flatpack-platform-sidebar') === 'collapsed')
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const [accountOpen, setAccountOpen] = useState(false)
  const [appearanceOpen, setAppearanceOpen] = useState(false)
  const [theme, setTheme] = useState<Theme>(() => {
    const stored = localStorage.getItem('flatpack-theme')
    return stored === 'light' || stored === 'dark' || stored === 'custom' || stored === 'system' ? stored : 'system'
  })
  const [shellColor, setShellColor] = useState(() => {
    const stored = localStorage.getItem('flatpack-shell-color')
    return stored && /^#[0-9a-f]{6}$/i.test(stored) ? stored : defaultShellColor
  })
  const session = useQuery({ queryKey: ['me', 'session'], queryFn: () => customFetch<Session>('/api/v1/auth/session', { method: 'GET' }), retry: false })
  const logout = useMutation({
    mutationFn: () => customFetch<void>('/api/v1/auth/logout', { method: 'POST' }),
    onSuccess: () => { queryClient.clear(); window.location.assign('/login') },
  })

  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)')
    const apply = () => applyAppearance(document.documentElement, theme, shellColor, media.matches)
    apply()
    media.addEventListener('change', apply)
    localStorage.setItem('flatpack-theme', theme)
    localStorage.setItem('flatpack-shell-color', shellColor)
    return () => media.removeEventListener('change', apply)
  }, [shellColor, theme])

  useEffect(() => { localStorage.setItem('flatpack-platform-sidebar', collapsed ? 'collapsed' : 'expanded') }, [collapsed])
  useEffect(() => {
    if (session.isError || (session.data && !session.data.hasPlatformAccess && !session.data.isPlatformAdministrator)) window.location.replace('/login')
  }, [session.data, session.isError])
  useEffect(() => {
    const close = (event: PointerEvent) => { if (accountMenu.current && !accountMenu.current.contains(event.target as Node)) setAccountOpen(false) }
    window.addEventListener('pointerdown', close)
    return () => window.removeEventListener('pointerdown', close)
  }, [])

  const identity = session.data?.displayName || session.data?.email || 'Account'
  const visibleNavigation = platformNavigation.filter((item) => session.data?.isPlatformAdministrator || session.data?.platformPermissions?.includes(item.permission))
  const currentLabel = platformNavigation.find((item) => 'exact' in item && item.exact ? pathname === item.to : pathname.startsWith(item.to))?.label ?? 'Administration'

  return <div className={`app-shell platform-shell${collapsed ? ' is-collapsed' : ''}${mobileNavOpen ? ' is-mobile-nav-open' : ''}`}>
    <aside className="sidebar" id="platform-navigation">
      <div className="mobile-sidebar-heading">
        <Link className="brand" to="/dashboard" aria-label="Flatpack platform overview" onClick={() => setMobileNavOpen(false)}><span className="brand-mark"><FlatpackLogo size={17} /></span><span className="sidebar-label">Flatpack</span></Link>
        <button className="mobile-nav-close" type="button" aria-label="Close navigation" onClick={() => setMobileNavOpen(false)}><X size={19} /></button>
      </div>
      <nav aria-label="Platform administration">
        <section className="sidebar-nav-section" aria-labelledby="platform-navigation-label">
          <span className="sidebar-label sidebar-section-label" id="platform-navigation-label">Administration</span>
          <div>{visibleNavigation.map(({ to, label, icon: Icon, ...item }) => <Link key={label} to={to} aria-label={label} title={label} activeOptions={{ exact: 'exact' in item && item.exact }} activeProps={{ className: 'active' }} onClick={() => setMobileNavOpen(false)}><Icon size={16} /><span className="sidebar-label">{label}</span></Link>)}</div>
        </section>
      </nav>
      <div className="sidebar-bottom" ref={accountMenu}>
        <div className="account-menu" data-open={accountOpen} role="dialog" aria-label="Platform account menu" aria-hidden={!accountOpen} inert={!accountOpen ? true : undefined}>
          <div className="account-menu-identity"><strong>{identity}</strong><span>{session.data?.email}</span></div>
          <Link className="account-menu-item" to="/dashboard/profile" onClick={() => setAccountOpen(false)}><UserRound size={17} /> Profile</Link>
          <button className="account-menu-item" type="button" aria-expanded={appearanceOpen} onClick={() => setAppearanceOpen((open) => !open)}><Palette size={17} /> Appearance <ChevronDown className={`menu-chevron${appearanceOpen ? ' is-open' : ''}`} size={15} /></button>
          <div className="appearance-collapse" data-open={appearanceOpen} aria-hidden={!appearanceOpen} inert={!appearanceOpen ? true : undefined}><div><div className="appearance-panel">
            <span className="appearance-label">Theme</span>
            {([['light', Sun], ['system', Monitor], ['dark', Moon], ['custom', Palette]] as const).map(([value, Icon]) => <button key={value} type="button" className={theme === value ? 'selected' : ''} onClick={() => setTheme(value)}><Icon size={15} /><span>{value[0].toUpperCase() + value.slice(1)}{value === 'custom' && <small>{shellColor.toUpperCase()}</small>}</span><span>{theme === value ? '✓' : ''}</span></button>)}
            {theme === 'custom' && <label className="shell-color-control"><span className="shell-color-well" style={{ backgroundColor: shellColor }}><input type="color" value={shellColor} aria-label="Custom shell color" onChange={(event) => { setShellColor(event.target.value); setTheme('custom') }} /></span><span><strong>Shell color</strong><small>Choose any base color</small></span></label>}
          </div></div></div>
          <div className="account-menu-separator" />
          <button className="account-menu-item danger-text" type="button" disabled={logout.isPending} onClick={() => logout.mutate()}><LogOut size={17} /> {logout.isPending ? 'Logging out…' : 'Log out'}</button>
        </div>
        <div className="sidebar-controls">
          <button className="account-trigger" type="button" disabled={!session.data} aria-label={`Account menu for ${identity}`} aria-expanded={accountOpen} onClick={() => setAccountOpen((open) => !open)}><span className="avatar">{initials(identity)}</span><span className="sidebar-label account-trigger-name">{identity}</span><ChevronDown className={`sidebar-label account-trigger-chevron${accountOpen ? ' is-open' : ''}`} size={14} /></button>
          <button className="collapse-trigger" type="button" aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} onClick={() => setCollapsed((value) => !value)}>{collapsed ? <PanelLeftOpen size={19} /> : <PanelLeftClose size={19} />}</button>
        </div>
      </div>
    </aside>
    <button className="mobile-nav-backdrop" type="button" aria-label="Close navigation" tabIndex={mobileNavOpen ? 0 : -1} onClick={() => setMobileNavOpen(false)} />
    <main className="workspace">
      <header className="topbar platform-topbar"><button className="mobile-nav-trigger" type="button" aria-label="Open navigation" aria-controls="platform-navigation" aria-expanded={mobileNavOpen} onClick={() => setMobileNavOpen(true)}><Menu size={19} /></button><span className="platform-topbar-title">{currentLabel}</span><Button variant="ghost" aria-label="Activity"><Bell size={16} /></Button></header>
      <section className="page">
        {!session.data?.hasPlatformAccess && !session.data?.isPlatformAdministrator ? <div className="shell-loading"><Skeleton /><Skeleton /><Skeleton /></div> : <Outlet />}
      </section>
    </main>
  </div>
}
