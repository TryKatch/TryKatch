import { createRootRoute, createRoute, createRouter, Outlet, redirect } from '@tanstack/react-router'
import { lazy, Suspense, type ElementType } from 'react'
import { AppShell } from './shell/AppShell'
import { PlatformShell } from './shell/PlatformShell'
import { LoginPage } from './views/LoginPage'
import { ForgotPasswordPage } from './views/ForgotPasswordPage'
import { ResetPasswordPage } from './views/ResetPasswordPage'
import { workspaceModules } from './modules'
import { FlatpackModuleProvider } from './module-system/ModuleExtensionSlot'

const pages = () => import('./views/Pages')
const DashboardPage = lazy(() => pages().then((module) => ({ default: module.DashboardPage })))
const UserManagementPage = lazy(() => pages().then((module) => ({ default: module.UserManagementPage })))
const AuditPage = lazy(() => pages().then((module) => ({ default: module.AuditPage })))
const ProfilePage = lazy(() => pages().then((module) => ({ default: module.ProfilePage })))
const PlatformOverviewPage = lazy(() => pages().then((module) => ({ default: module.PlatformOverviewPage })))
const PlatformOrganizationsPage = lazy(() => pages().then((module) => ({ default: module.PlatformOrganizationsPage })))
const PlatformUsersPage = lazy(() => pages().then((module) => ({ default: module.PlatformUsersPage })))
const PlatformAuthenticationPage = lazy(() => pages().then((module) => ({ default: module.PlatformAuthenticationPage })))
const AcceptInvitationPage = lazy(() => pages().then((module) => ({ default: module.AcceptInvitationPage })))
const PlatformAccessActivationPage = lazy(() => pages().then((module) => ({ default: module.PlatformAccessActivationPage })))
const ArchivePage = lazy(() => import('./features/archive/ArchivePage').then((module) => ({ default: module.ArchivePage })))

function withSuspense(Page: ElementType) {
  return function LazyRoutePage() {
    return <Suspense fallback={<div className="module-loading" role="status">Loading…</div>}><Page /></Suspense>
  }
}

const rootRoute = createRootRoute({ component: () => <FlatpackModuleProvider catalog={workspaceModules}><Outlet /></FlatpackModuleProvider> })
const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: '/login', component: LoginPage })
const forgotPasswordRoute = createRoute({ getParentRoute: () => rootRoute, path: '/forgot-password', component: ForgotPasswordPage })
const resetPasswordRoute = createRoute({ getParentRoute: () => rootRoute, path: '/reset-password', component: ResetPasswordPage })
const workspaceRoute = createRoute({ getParentRoute: () => rootRoute, id: '_workspace', component: AppShell })
const overviewRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/overview', component: withSuspense(DashboardPage) })
const workspaceModuleRoutes = workspaceModules.routes.map((route) => createRoute({
  getParentRoute: () => workspaceRoute,
  path: route.path,
  component: withSuspense(route.component),
}))
const userManagementRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/user-management', component: withSuspense(UserManagementPage) })
const legacyTeamRoute = createRoute({ getParentRoute: () => rootRoute, path: '/team', beforeLoad: () => { throw redirect({ to: '/user-management', replace: true }) } })
const auditRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/audit', component: withSuspense(AuditPage) })
const archiveRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/archive', component: withSuspense(ArchivePage) })
const profileRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/profile', component: withSuspense(ProfilePage) })
const legacySettingsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings', beforeLoad: () => { throw redirect({ to: '/profile', replace: true }) } })
const platformRoute = createRoute({ getParentRoute: () => rootRoute, path: '/dashboard', component: PlatformShell })
const platformOverviewRoute = createRoute({ getParentRoute: () => platformRoute, path: '/', component: withSuspense(PlatformOverviewPage) })
const tenantDirectoryRoute = createRoute({ getParentRoute: () => platformRoute, path: 'tenants', component: withSuspense(PlatformOrganizationsPage) })
const platformUsersRoute = createRoute({ getParentRoute: () => platformRoute, path: 'users', component: withSuspense(PlatformUsersPage) })
const platformInvitationsRoute = createRoute({ getParentRoute: () => platformRoute, path: 'invitations', beforeLoad: () => { throw redirect({ to: '/dashboard/users', replace: true }) } })
const platformAuthenticationRoute = createRoute({ getParentRoute: () => platformRoute, path: 'authentication', component: withSuspense(PlatformAuthenticationPage) })
const platformProfileRoute = createRoute({ getParentRoute: () => platformRoute, path: 'profile', component: withSuspense(ProfilePage) })
const invitationRoute = createRoute({ getParentRoute: () => rootRoute, path: '/invite/$token', component: withSuspense(AcceptInvitationPage) })
const platformActivationRoute = createRoute({ getParentRoute: () => rootRoute, path: '/activate-access', component: withSuspense(PlatformAccessActivationPage) })

const routeTree = rootRoute.addChildren([
  loginRoute,
  forgotPasswordRoute,
  resetPasswordRoute,
  platformRoute.addChildren([platformOverviewRoute, tenantDirectoryRoute, platformUsersRoute, platformInvitationsRoute, platformAuthenticationRoute, platformProfileRoute]),
  legacyTeamRoute,
  legacySettingsRoute,
  invitationRoute,
  platformActivationRoute,
  workspaceRoute.addChildren([overviewRoute, ...workspaceModuleRoutes, userManagementRoute, auditRoute, archiveRoute, profileRoute]),
])

export const router = createRouter({ routeTree, defaultPreload: 'intent' })

declare module '@tanstack/react-router' {
  interface Register { router: typeof router }
}
