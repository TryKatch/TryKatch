import { createRootRoute, createRoute, createRouter, Outlet, redirect } from '@tanstack/react-router'
import { AppShell } from './shell/AppShell'
import { PlatformShell } from './shell/PlatformShell'
import { LoginPage } from './views/LoginPage'
import { ForgotPasswordPage } from './views/ForgotPasswordPage'
import { ResetPasswordPage } from './views/ResetPasswordPage'
import { AcceptInvitationPage, AuditPage, DashboardPage, PlatformAccessActivationPage, PlatformAuthenticationPage, PlatformOrganizationsPage, PlatformOverviewPage, PlatformUsersPage, ProfilePage, ProjectsPage, UserManagementPage } from './views/Pages'
import { ArchivePage } from './features/archive/ArchivePage'

const rootRoute = createRootRoute({ component: () => <Outlet /> })
const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: '/login', component: LoginPage })
const forgotPasswordRoute = createRoute({ getParentRoute: () => rootRoute, path: '/forgot-password', component: ForgotPasswordPage })
const resetPasswordRoute = createRoute({ getParentRoute: () => rootRoute, path: '/reset-password', component: ResetPasswordPage })
const workspaceRoute = createRoute({ getParentRoute: () => rootRoute, id: '_workspace', component: AppShell })
const overviewRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/overview', component: DashboardPage })
const projectsRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/projects', component: ProjectsPage })
const userManagementRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/user-management', component: UserManagementPage })
const legacyTeamRoute = createRoute({ getParentRoute: () => rootRoute, path: '/team', beforeLoad: () => { throw redirect({ to: '/user-management', replace: true }) } })
const auditRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/audit', component: AuditPage })
const archiveRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/archive', component: ArchivePage })
const profileRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/profile', component: ProfilePage })
const legacySettingsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings', beforeLoad: () => { throw redirect({ to: '/profile', replace: true }) } })
const platformRoute = createRoute({ getParentRoute: () => rootRoute, path: '/dashboard', component: PlatformShell })
const platformOverviewRoute = createRoute({ getParentRoute: () => platformRoute, path: '/', component: PlatformOverviewPage })
const tenantDirectoryRoute = createRoute({ getParentRoute: () => platformRoute, path: 'tenants', component: PlatformOrganizationsPage })
const platformUsersRoute = createRoute({ getParentRoute: () => platformRoute, path: 'users', component: PlatformUsersPage })
const platformInvitationsRoute = createRoute({ getParentRoute: () => platformRoute, path: 'invitations', beforeLoad: () => { throw redirect({ to: '/dashboard/users', replace: true }) } })
const platformAuthenticationRoute = createRoute({ getParentRoute: () => platformRoute, path: 'authentication', component: PlatformAuthenticationPage })
const platformProfileRoute = createRoute({ getParentRoute: () => platformRoute, path: 'profile', component: ProfilePage })
const invitationRoute = createRoute({ getParentRoute: () => rootRoute, path: '/invite/$token', component: AcceptInvitationPage })
const platformActivationRoute = createRoute({ getParentRoute: () => rootRoute, path: '/activate-access', component: PlatformAccessActivationPage })

const routeTree = rootRoute.addChildren([
  loginRoute,
  forgotPasswordRoute,
  resetPasswordRoute,
  platformRoute.addChildren([platformOverviewRoute, tenantDirectoryRoute, platformUsersRoute, platformInvitationsRoute, platformAuthenticationRoute, platformProfileRoute]),
  legacyTeamRoute,
  legacySettingsRoute,
  invitationRoute,
  platformActivationRoute,
  workspaceRoute.addChildren([overviewRoute, projectsRoute, userManagementRoute, auditRoute, archiveRoute, profileRoute]),
])

export const router = createRouter({ routeTree, defaultPreload: 'intent' })

declare module '@tanstack/react-router' {
  interface Register { router: typeof router }
}
