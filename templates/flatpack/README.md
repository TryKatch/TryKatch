# FlatpackApp

> Ship flat. Assemble fast.

FlatpackApp is a clean-room enterprise application foundation for .NET 10, PostgreSQL, React, TanStack, Aspire, and the open Grafana observability stack.

## Start locally

Prerequisites: .NET SDK 10.0.301+, Docker, Node.js 24+, and pnpm 10.

```bash
dotnet tool restore
dotnet restore FlatpackApp.slnx
pnpm --dir web install --frozen-lockfile
dotnet run --project src/FlatpackApp.AppHost
```

Before the first API run, apply the migrations with the migrator role as described in [docs/database.md](docs/database.md). Set `Bootstrap__PlatformAdminEmail` and `Bootstrap__PlatformAdminPassword` only for a controlled bootstrap operation, then remove them.

## Architecture

- `Domain` contains framework-free organization, membership, role, invitation, audit, and Project models.
- `Application` contains focused use cases, validation, permissions, and outbound interfaces.
- `Infrastructure` owns EF Core, PostgreSQL RLS, auditing, the transactional outbox, and selected adapters.
- `Identity` owns ASP.NET Core Identity, OpenIddict, MFA primitives, session cookies, and data-protection keys.
- `Api` owns controllers, HTTP contracts, BFF endpoints, middleware, and composition.
- `ServiceDefaults` owns OpenTelemetry, health, discovery, and resilient HTTP defaults.
- `AppHost` orchestrates development and tests only.
- `web/apps/web` is the React application; `web/packages/ui` is the owned component system; `web/packages/api-client` is machine-generated from OpenAPI.

The customer-facing workspace word is **organization**. Platform administrators use the dedicated **Tenant Management** console at `/dashboard/tenants`; ordinary workspace navigation does not expose a tenant selector.

## Security invariants

- Browser and API resource routes are tenant-neutral. Middleware resolves a protected server-issued workspace cookie (or a trusted `organization_id` claim for external clients) and revalidates active membership for every scoped request.
- Platform administration uses immutable Administrator, Operator, and Auditor defaults plus validated custom roles expanded from the API-published `platform.resource.action` permission catalog; it never trusts organization membership permissions.
- Platform access invitations use 24-hour Identity activation tokens. Suspending or changing access invalidates existing sessions, and the final active administrator cannot be removed.
- Tenant provisioning creates a seven-day Owner invitation. Platform operators do not receive implicit workspace membership; the invited owner creates an account or signs in before middleware establishes tenant context.
- Organization access is resolved from the authenticated actor plus protected workspace context.
- Every scoped transaction sets PostgreSQL `app.organization_id` and `app.actor_id`; RLS fails closed without them.
- Runtime database credentials cannot own tables or bypass RLS.
- Browser authentication uses secure HttpOnly cookies and antiforgery. Browser code never receives access or refresh tokens.
- External clients use OpenIddict authorization code + PKCE or client credentials. Implicit and password grants are not enabled.

## Development commands

```bash
dotnet restore FlatpackApp.slnx
dotnet build FlatpackApp.slnx --no-restore
dotnet test tests/FlatpackApp.UnitTests
pnpm --dir web generate
pnpm --dir web typecheck
pnpm --dir web test
pnpm --dir web build
```

Generated files under `web/packages/api-client/src/generated` are machine-owned. Change API contracts, rebuild the API, and run `pnpm --dir web generate`; never hand-edit those files.

See [database operations](docs/database.md) and [deployment/observability](docs/operations.md) before shipping.
