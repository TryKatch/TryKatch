import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { customFetch } from '@trykatchapp/api-client'
import type { TrykatchNavigationContribution } from '@trykatchapp/module-sdk'
import { Button, Skeleton } from '@trykatchapp/ui'
import { Bell, Building2, ChevronDown, LayoutDashboard, LogOut, Menu, Monitor, Moon, Palette, PanelLeftClose, PanelLeftOpen, ShieldCheck, Sun, UserRound, Users, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { TrykatchLogo } from '../components/TrykatchLogo'
import { SignOutDialog } from '../components/SignOutDialog'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'
import { useI18n } from '../i18n/I18nProvider'
import { workspaceModules } from '../modules'
import { applyAppearance, defaultShellColor, type Theme } from './appearance'

interface Session { displayName: string; email: string; hasPlatformAccess?: boolean; isPlatformAdministrator?: boolean; platformPermissions?: string[] }

const corePlatformNavigation: readonly TrykatchNavigationContribution[] = [
  { id: 'platform.overview', surface: 'platform', section: 'Administration', order: 10, to: '/dashboard', label: 'Platform overview', icon: LayoutDashboard, requiredPermission: 'platform.dashboard.read', exact: true },
  { id: 'platform.tenants', surface: 'platform', section: 'Administration', order: 20, to: '/dashboard/tenants', label: 'Tenant management', icon: Building2, requiredPermission: 'platform.tenants.read' },
  { id: 'platform.users', surface: 'platform', section: 'Administration', order: 30, to: '/dashboard/users', label: 'User management', icon: Users, requiredPermission: 'platform.users.read' },
  { id: 'platform.authentication', surface: 'platform', section: 'Administration', order: 40, to: '/dashboard/authentication', label: 'Authentication', icon: ShieldCheck, requiredPermission: 'platform.authentication.read' },
]
const platformNavigation = [...corePlatformNavigation, ...workspaceModules.navigationFor('platform')]
  .toSorted((left, right) => left.order - right.order || left.id.localeCompare(right.id))

function initials(value: string) {
  return value.split(/\s+/).filter(Boolean).map((part) => part[0]).join('').slice(0, 2).toUpperCase() || 'U'
}

export function PlatformShell() {
  const { t } = useI18n()
  const pathname = useRouterState({ select: (state) => state.location.pathname })
  const queryClient = useQueryClient()
  const accountMenu = useRef<HTMLDivElement>(null)
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('trykatch-platform-sidebar') === 'collapsed')
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const [accountOpen, setAccountOpen] = useState(false)
  const [signOutOpen, setSignOutOpen] = useState(false)
  const [appearanceOpen, setAppearanceOpen] = useState(false)
  const [theme, setTheme] = useState<Theme>(() => {
    const stored = localStorage.getItem('trykatch-theme')
    return stored === 'light' || stored === 'dark' || stored === 'custom' || stored === 'system' ? stored : 'system'
  })
  const [shellColor, setShellColor] = useState(() => {
    const stored = localStorage.getItem('trykatch-shell-color')
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
    localStorage.setItem('trykatch-theme', theme)
    localStorage.setItem('trykatch-shell-color', shellColor)
    return () => media.removeEventListener('change', apply)
  }, [shellColor, theme])

  useEffect(() => { localStorage.setItem('trykatch-platform-sidebar', collapsed ? 'collapsed' : 'expanded') }, [collapsed])
  useEffect(() => {
    if (session.isError || (session.data && !session.data.hasPlatformAccess && !session.data.isPlatformAdministrator)) window.location.replace('/login')
  }, [session.data, session.isError])
  useEffect(() => {
    const close = (event: PointerEvent) => { if (accountMenu.current && !accountMenu.current.contains(event.target as Node)) setAccountOpen(false) }
    window.addEventListener('pointerdown', close)
    return () => window.removeEventListener('pointerdown', close)
  }, [])

  const identity = session.data?.displayName || session.data?.email || t('Account')
  const visibleNavigation = platformNavigation.filter((item) => session.data?.isPlatformAdministrator || !item.requiredPermission || session.data?.platformPermissions?.includes(item.requiredPermission))
  const currentLabel = platformNavigation.find((item) => item.exact ? pathname === item.to : pathname.startsWith(item.to))?.label ?? 'Administration'

  return <div className={`app-shell platform-shell${collapsed ? ' is-collapsed' : ''}${mobileNavOpen ? ' is-mobile-nav-open' : ''}`}>
    <aside className="sidebar" id="platform-navigation">
      <div className="mobile-sidebar-heading">
        <Link className="brand" to="/dashboard" aria-label={`Trykatch ${t('Platform overview')}`} onClick={() => setMobileNavOpen(false)}><span className="brand-mark"><TrykatchLogo size={17} /></span><span className="sidebar-label">Trykatch</span></Link>
        <button className="mobile-nav-close" type="button" aria-label={t('Close navigation')} onClick={() => setMobileNavOpen(false)}><X size={19} /></button>
      </div>
      <nav aria-label={t('Platform administration')}>
        <section className="sidebar-nav-section" aria-labelledby="platform-navigation-label">
          <span className="sidebar-label sidebar-section-label" id="platform-navigation-label">{t('Administration')}</span>
          <div>{visibleNavigation.map(({ id, to, label, icon: Icon, exact }) => <Link key={id} to={to} aria-label={t(label)} title={t(label)} activeOptions={{ exact }} activeProps={{ className: 'active' }} onClick={() => setMobileNavOpen(false)}><Icon size={16} /><span className="sidebar-label">{t(label)}</span></Link>)}</div>
        </section>
      </nav>
      <div className="sidebar-bottom" ref={accountMenu}>
        <div className="account-menu" data-open={accountOpen} role="dialog" aria-label={t('Account and appearance')} aria-hidden={!accountOpen} inert={!accountOpen ? true : undefined}>
          <div className="account-menu-identity"><strong>{identity}</strong><span>{session.data?.email}</span></div>
          <Link className="account-menu-item" to="/dashboard/profile" onClick={() => setAccountOpen(false)}><UserRound size={17} /> {t('Profile')}</Link>
          <button className="account-menu-item" type="button" aria-expanded={appearanceOpen} onClick={() => setAppearanceOpen((open) => !open)}><Palette size={17} /> {t('Appearance')} <ChevronDown className={`menu-chevron${appearanceOpen ? ' is-open' : ''}`} size={15} /></button>
          <div className="appearance-collapse" data-open={appearanceOpen} aria-hidden={!appearanceOpen} inert={!appearanceOpen ? true : undefined}><div><div className="appearance-panel">
            <span className="appearance-label">{t('Theme')}</span>
            {([['light', Sun, 'Light'], ['system', Monitor, 'System'], ['dark', Moon, 'Dark'], ['custom', Palette, 'Custom']] as const).map(([value, Icon, label]) => <button key={value} type="button" className={theme === value ? 'selected' : ''} onClick={() => setTheme(value)}><Icon size={15} /><span>{t(label)}{value === 'custom' && <small>{shellColor.toUpperCase()}</small>}</span><span>{theme === value ? '✓' : ''}</span></button>)}
            {theme === 'custom' && <label className="shell-color-control"><span className="shell-color-well" style={{ backgroundColor: shellColor }}><input type="color" value={shellColor} aria-label={`${t('Custom')} ${t('Shell color')}`} onChange={(event) => { setShellColor(event.target.value); setTheme('custom') }} /></span><span><strong>{t('Shell color')}</strong><small>{t('Choose any base color')}</small></span></label>}
            <LanguageSwitcher />
          </div></div></div>
          <div className="account-menu-separator" />
          <button className="account-menu-item danger-text" type="button" onClick={() => { setAccountOpen(false); setSignOutOpen(true) }}><LogOut size={17} /> {t('Log out')}</button>
        </div>
        <div className="sidebar-controls">
          <button className="account-trigger" type="button" disabled={!session.data} aria-label={`${t('Account')}: ${identity}`} aria-expanded={accountOpen} onClick={() => setAccountOpen((open) => !open)}><span className="avatar">{initials(identity)}</span><span className="sidebar-label account-trigger-name">{identity}</span><ChevronDown className={`sidebar-label account-trigger-chevron${accountOpen ? ' is-open' : ''}`} size={14} /></button>
          <button className="collapse-trigger" type="button" aria-label={t(collapsed ? 'Expand sidebar' : 'Collapse sidebar')} onClick={() => setCollapsed((value) => !value)}>{collapsed ? <PanelLeftOpen size={19} /> : <PanelLeftClose size={19} />}</button>
        </div>
      </div>
    </aside>
    <button className="mobile-nav-backdrop" type="button" aria-label={t('Close navigation')} tabIndex={mobileNavOpen ? 0 : -1} onClick={() => setMobileNavOpen(false)} />
    <main className="workspace">
      <header className="topbar platform-topbar"><button className="mobile-nav-trigger" type="button" aria-label={t('Open navigation')} aria-controls="platform-navigation" aria-expanded={mobileNavOpen} onClick={() => setMobileNavOpen(true)}><Menu size={19} /></button><span className="platform-topbar-title">{t(currentLabel)}</span><Button variant="ghost" aria-label={t('Audit')}><Bell size={16} /></Button></header>
      <section className="page">
        {!session.data?.hasPlatformAccess && !session.data?.isPlatformAdministrator ? <div className="shell-loading"><Skeleton /><Skeleton /><Skeleton /></div> : <Outlet />}
      </section>
    </main>
    <SignOutDialog open={signOutOpen} identity={identity} isPending={logout.isPending} error={logout.error?.message} onOpenChange={setSignOutOpen} onConfirm={() => logout.mutate()} />
  </div>
}
