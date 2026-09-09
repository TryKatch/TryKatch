# Trykatch UAT Report

Date: 2026-09-07  
Branch: `feat/trykatch-enterprise-template`
Result: **Conditional fail — not ready for release**

The core template, architecture, PostgreSQL isolation, primary business workflows, MFA authenticator flow, and package-generation matrix passed. Release should wait for the three high-severity defects below: recovery-code login is unusable, antiforgery failures are reported as HTTP 500, and the UI exposes organization-management controls that the current organization role cannot use without explaining the resulting 403.

## Environment and scope

- .NET SDK 10.0.301
- Node.js 24.18.1 and pnpm 10.17.1
- Isolated PostgreSQL 14.19 cluster on a disposable local port
- Separate migrator and runtime PostgreSQL roles; both configured with `NOBYPASSRLS`
- Chromium driven through Playwright CLI
- Real API, Vite proxy, secure cookie authentication, and PostgreSQL persistence
- Docker daemon was unavailable, so production container builds, Testcontainers, and end-to-end Grafana/Loki/Tempo ingestion were not executed locally

The isolated API, Vite process, browser session, and PostgreSQL server were stopped after testing. No existing local database was changed.

## Passed acceptance scenarios

| Area | Result | Evidence |
| --- | --- | --- |
| Canonical .NET build | Pass | Entire solution built with 0 warnings and 0 errors under `-warnaserror`. |
| React client generation | Pass | OpenAPI regenerated through Orval; TypeScript typecheck and production Vite build passed. |
| Automated tests | Pass | 6 .NET unit tests, 1 Vitest test, and 1 Chromium/axe login test passed. |
| Template package | Pass | Fresh `Trykatch.Templates.0.1.0.nupkg` created; package contains 196 files and no `bin`, `obj`, `node_modules`, `dist`, or `.tsbuildinfo` content. |
| Template matrix | Pass | Default React, dotted/hyphenated `--ui none`, email, storage, documents, images, and all options together generated, restored, and built with 0 warnings. |
| Transformed web name | Pass | `Acme.Tools-Portal` generated with React; frozen install, typecheck, and production build passed. |
| Database migrations | Pass | `identity`, `platform`, and `app` contexts migrated using the migrator role. |
| Runtime database role | Pass | Runtime role operated without table ownership or RLS bypass. |
| PostgreSQL RLS | Pass | Runtime role saw 0 projects with no scope, 1 project under `uat-org`, and 0 under the other organization. Audit rows were isolated the same way. |
| Organization boundary | Pass | Protected workspace context returned 200; missing context returned 409 and unauthorized membership returned 403. |
| Login and session | Pass | Secure cookie login, server-side session lookup, and same-origin Vite proxy worked. |
| Account lockout | Pass | Attempts 1–4 returned 401, attempt 5 and a subsequent correct password returned 423. |
| TOTP MFA | Pass | Setup generated a shared key and recovery codes; a real current TOTP completed a second-factor login. |
| Organization administration | Pass | Platform administrator created an organization and automatically became Owner. |
| Invitations | Pass | Invitation creation returned a one-time token; a second verified account accepted it and became a Member. |
| Membership lifecycle | Pass | Owner suspended and reactivated the invited Member. |
| Project reference feature | Pass | Create, list, edit, and archive completed through the browser; all actions appeared in audit history and outbox dispatch logs. |
| Custom roles | Pass | Created `Project Operator`, assigned three permissions, then updated it to four permissions. |
| Audit activity | Pass | Project, invitation, role, and membership events were persisted and displayed. |
| Workspace entry | Pass | Tenant Management selects a protected workspace context before navigating to neutral `/overview`, `/projects`, and `/user-management` routes. Platform administration remains under `/dashboard`. |
| Theme and command palette | Pass | Dark theme persisted and `Cmd+K` opened keyboard navigation. |
| OpenAPI | Pass | OpenAPI 3.1.1; 25 operations, 0 missing operation IDs, and 0 duplicate operation IDs. |
| OpenIddict client credentials | Pass | Configured confidential client received a bearer token with HTTP 200. |
| Disabled grants | Pass | Password grant returned `unsupported_grant_type`; implicit response type returned `unsupported_response_type`. |
| PKCE enforcement | Pass | Public authorization-code client was stored with `ft:pkce`; a request without `code_challenge` returned 400 `invalid_request`. |
| Health endpoints | Pass | `/health/live` and `/health/ready` returned 200 with minimal `Healthy` bodies. |
| Deployment configuration | Partial pass | Compose configuration and provisioned Grafana dashboard JSON validated without starting containers. |

## Release blockers

### UAT-001 — Recovery-code login always fails

Severity: **High**

After successful MFA enrollment, an exact newly issued recovery code was submitted using “This is a recovery code.” The API returned 401 `Invalid multi-factor code`.

The controller normalizes both authenticator and recovery codes by removing hyphens before calling `TwoFactorRecoveryCodeSignInAsync`. ASP.NET Identity recovery codes are generated and stored with the separator, so this transformation prevents redemption.

Acceptance condition: a newly generated recovery code signs in exactly once, reuse is rejected, and remaining-code count is updated.

### UAT-002 — Antiforgery rejection becomes HTTP 500

Severity: **High**

A cookie-authenticated project write without `X-CSRF-TOKEN` was blocked, but `AntiforgeryValidationException` escaped the authorization filter and produced HTTP 500. Sending the wrong antiforgery header to login produced the same result.

The security control did prevent mutation, but this violates the Problem Details contract, records expected client errors as server failures, and can create noisy alerts.

Acceptance condition: missing, invalid, or expired antiforgery tokens return a stable 400 or 403 Problem Details response and never log an unhandled exception.

### UAT-003 — UI actions do not reflect organization permissions

Severity: **High**

An accepted organization Member saw `Invite member`, `Suspend`, and role-management controls. Attempting to suspend another member correctly returned 403 from the API, but the page showed no error and left the unusable control visible.

This confirms API authorization is working, but the React authorization experience is incomplete.

Acceptance condition: route access and action visibility are derived from effective organization permissions; a race or stale-permission 403 is surfaced as an accessible Problem Details message or toast.

## Other findings

| ID | Severity | Finding |
| --- | --- | --- |
| UAT-004 | Medium | Direct navigation to an authenticated organization route while signed out renders the application shell and placeholder content while API calls return 401; it should redirect to login or show an explicit signed-out state. No protected records were exposed. |
| UAT-005 | Medium | Dashboard counts, recent activity, and health are hard-coded (`12` projects, `28` members, `184` events, all healthy) and contradict the real database. These must be clearly marked demo data or replaced with real queries before release. |
| UAT-006 | Medium | There is a logout API but no visible logout action in the React application. |
| UAT-007 | Medium | When password login transitions to MFA, the verification-code field is prefilled with the user's email address. |
| UAT-008 | Medium | At 390px width, navigation icons lose accessible names and collection tables clip right-side status/actions. The mobile layout does not yet meet the stated keyboard/WCAG experience. |
| UAT-009 | Low | Create Organization and Create/Edit Role dialogs emit Radix warnings because they have no description or explicit `aria-describedby={undefined}`. |
| UAT-010 | Low | Audit activity displays raw actor and subject UUIDs instead of human-readable identities and links. |
| UAT-011 | Low | `/favicon.ico` returns 404 during every browser session. |

## Not executed locally

- Testcontainers integration suite and container image builds, because the Docker daemon was unavailable.
- Live Aspire orchestration of Collector, Prometheus, Loki, Tempo, and Grafana.
- Assertions for one-log-per-event, Loki/Tempo ingestion, Prometheus scraping, and Grafana datasource connectivity.
- A complete external authorization-code redirect/login/callback exchange. Client registration, PKCE enforcement, and disabled grants were verified.
- Production non-root/read-only filesystem behavior and image health checks.

These remain mandatory release-gate work; they are environmental omissions, not passes.

## Evidence

- [Login surface](../output/playwright/01-login.png)
- [Desktop dashboard](../output/playwright/02-dashboard.png)
- [Dark theme](../output/playwright/03-dark-theme.png)
- [390px membership view](../output/playwright/04-mobile-members.png)
- Package: `artifacts/packages/Trykatch.Templates.0.1.0.nupkg`
- SHA-256: `9760d24f581fee3993e39b4f9c945cb3d8a5a4904da3c8bbbdfaf37ac409f025`

## Recommendation

Do not publish `0.1.0` yet. Fix UAT-001 through UAT-003 first, then address UAT-004 through UAT-008 and rerun this browser suite. After Docker is available, run the remaining container, Testcontainers, and observability release gates before approving the package.

## Recovery UX addendum

The 2026-09-07 recovery implementation was verified against the running API and React app:

- Sidebar navigation is grouped into Workspace, Administration, and Recovery at desktop width and in the 390px off-canvas navigation.
- Project and User Management tables request Active records only; Archive aggregates the explicit `recoverable` scope across projects, people, invitations, and roles.
- Archive loaded four live records, filtered them by resource type and recovery state, and exposed permission-aware View and Restore actions through a vertical kebab menu.
- A project was restored from Archive, appeared in the active Projects table, and was archived again. It returned to Archive with the same identifier and relationships.
- The reusable record-details dialog was verified with identity, state, removal context, structured fields, and a clear restore action.
- Browser console inspection reported zero errors and zero warnings.
- React typecheck, 12 Vitest assertions, production Vite build, 16 .NET unit tests, and the complete .NET solution build passed. The solution build produced zero warnings and zero errors.

The Testcontainers RLS test was attempted again but could not connect to Docker at either supported socket. This remains an environment blocker; the existing local PostgreSQL RLS UAT evidence above remains valid.
