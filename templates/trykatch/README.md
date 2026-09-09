# TrykatchApp

> Start secure. Build freely.

TrykatchApp is a clean-room enterprise application foundation for .NET 10, PostgreSQL, React, TanStack, Aspire, and the open Grafana observability stack.

## Start locally

Prerequisites: .NET SDK 10.0.301+, Docker, Node.js 24+, and pnpm 10.

```bash
dotnet tool restore
dotnet restore TrykatchApp.slnx
pnpm --dir web install --frozen-lockfile
dotnet run --project src/TrykatchApp.AppHost
```

AppHost runs the one-shot `Migrator` project before the API. Production Compose also waits for it and gives the API a separate runtime credential. The role setup and deployment contract are described in [docs/database.md](docs/database.md). Set `Bootstrap__PlatformAdminEmail` and `Bootstrap__PlatformAdminPassword` only for a controlled bootstrap operation, then remove them.

The development launch profiles and AppHost seed two local-only identities with the password `Admin@123`:

- `admin@trykatch.net` — platform Administrator access.
- `tenant@trykatch.net` — Owner access to the seeded Demo Workspace.

Demo seeding and its relaxed eight-character minimum are guarded by both the Development environment and `DevelopmentDemo__Enabled`; production retains the twelve-character minimum and never enables the demo path. The configured password is reapplied to these demo identities when the development API starts so local credentials remain predictable.

## Architecture

- `Domain` contains framework-free organization, membership, role, invitation, audit, and Project models.
- `Application` contains focused use cases, validation, permissions, and outbound interfaces.
- `Infrastructure` owns EF Core, PostgreSQL RLS, auditing, the transactional outbox, and selected adapters.
- `Identity` owns ASP.NET Core Identity, OpenIddict, MFA primitives, session cookies, and data-protection keys.
- `Migrator` applies ordered schema migrations, verifies the bootstrap-created runtime role, and grants it least-privilege data access.
- `Modules.Abstractions` defines the small install-time module seam and validates module identity and dependency graphs.
- `Api` owns controllers, HTTP contracts, BFF endpoints, middleware, and composition.
- `ServiceDefaults` owns OpenTelemetry, health, discovery, and resilient HTTP defaults.
- `AppHost` orchestrates development and tests only.
- `web/apps/web` is the React application; `web/packages/ui` is the owned component system; `web/packages/api-client` is machine-generated from OpenAPI.
- `web/packages/module-sdk` validates typed routes and navigation contributed by enabled full-stack modules.

Projects is the reference full-stack module. Its backend registration and permission definitions are activated through the explicit module registry, while its React route and navigation are contributed through the web module catalog. See [module authoring](docs/modules.md).

The customer-facing workspace word is **organization**. Platform administrators use the dedicated **Tenant Management** console at `/dashboard/tenants`; ordinary workspace navigation does not expose a tenant selector.

## Security invariants

- Browser and API resource routes are tenant-neutral. Middleware resolves a protected server-issued workspace cookie (or a trusted `organization_id` claim for external clients) and revalidates active membership for every scoped request.
- Platform administration uses immutable Administrator, Operator, and Auditor defaults plus validated custom roles expanded from the API-published `platform.resource.action` permission catalog; it never trusts organization membership permissions.
- Platform access invitations use 24-hour Identity activation tokens. Suspending or changing access invalidates existing sessions, and the final active administrator cannot be removed.
- Tenant provisioning creates a seven-day Owner invitation. Platform operators do not receive implicit workspace membership; the invited owner creates an account or signs in before middleware establishes tenant context.
- Workspace invitations return a complete one-time URL rather than a standalone token. The optional SMTP adapter sends that URL; otherwise an administrator can copy it. New invitees provide first and last name plus a password, while existing identities sign in before accepting.
- Organization access is resolved from the authenticated actor plus protected workspace context.
- Every scoped transaction sets PostgreSQL `app.organization_id` and `app.actor_id`; RLS fails closed without them.
- Runtime database credentials cannot own tables or bypass RLS.
- Browser authentication uses secure HttpOnly cookies and antiforgery. Browser code never receives access or refresh tokens.
- Password recovery returns an account-neutral response, rate-limits requests, invalidates existing sessions, and uses `TRYKATCH_PUBLIC_URL` as the trusted origin for production email links.
- External clients use OpenIddict authorization code + PKCE or client credentials. Implicit and password grants are not enabled.

## Development commands

```bash
dotnet restore TrykatchApp.slnx
dotnet build TrykatchApp.slnx --no-restore
dotnet test tests/TrykatchApp.UnitTests
pnpm --dir web generate
pnpm --dir web typecheck
pnpm --dir web test
pnpm --dir web build
```

Generated files under `web/packages/api-client/src/generated` are machine-owned. Change API contracts, rebuild the API, and run `pnpm --dir web generate`; never hand-edit those files.

Development API documentation is available at `/docs`, backed by the generated OpenAPI 3.1 contract at `/openapi/v1.json`. The same generation step produces the deny-by-default, provider-neutral AI tool contract in `docs/generated/assistant-contract.json`. See [AI-assisted development](docs/ai-assisted-development.md).

See [database operations](docs/database.md), [deployment/observability](docs/operations.md), and the [threat model](docs/threat-model.md) before shipping.
