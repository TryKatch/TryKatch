# Trykatch

> Start secure. Build freely.

Trykatch is a clean-room enterprise application foundation for .NET 10, PostgreSQL, React, TanStack, Aspire, and the open Grafana observability stack.

## Start locally

For deployment, first read [production identity](docs/production-identity.md): three certificate/password pairs are required, and an existing plaintext key ring needs an explicit privileged dry-run/apply before the API can start.

Prerequisites: .NET SDK 10.0.301+, Docker, Node.js 24+, and Corepack. The generated root `package.json` pins pnpm 10.17.1, so the same package-manager version is selected on every machine.

```bash
dotnet tool restore
dotnet restore Trykatch.slnx
corepack pnpm --dir web install --frozen-lockfile
trykatch start
```

If the Trykatch CLI is not installed, run `dotnet run --launch-profile https --project src/API/Trykatch.AppHost/Trykatch.AppHost.csproj`. In Rider, open `Trykatch.slnx` and run the **Trykatch.AppHost: https** profile. AppHost is the development startup project; it provisions PostgreSQL, runs migrations, and injects the separate least-privilege database connections before starting the API and React application. Do not run `Trykatch.Api` by itself.

AppHost runs the one-shot `Migrator` project before the API. Production Compose also waits for it and gives the API a separate runtime credential. The role setup and deployment contract are described in [docs/database.md](docs/database.md). Set `Bootstrap__PlatformAdminEmail` and `Bootstrap__PlatformAdminPassword` only for a controlled bootstrap operation, then remove them.

For Compose, copy `.env.example`, set the required immutable `TRYKATCH_RELEASE_VERSION`, and replace every secret placeholder. The web port and Grafana bind to loopback by default. Connect the public TLS ingress directly to the Compose network at the dedicated `TRYKATCH_INGRESS_PROXY_IP`; the gateway, ingress, and web-proxy addresses are reserved outside Docker's automatic allocation range so host-published traffic and unrelated services cannot impersonate the ingress. The API accepts the normalized scheme/client pair only from the explicitly configured web proxy. The base OTLP path is private single-host plaintext; use `compose.observability-tls.yml` for authenticated TLS ingestion. See [operations](docs/operations.md) for ingress trust, sampling, privacy, queues, retention, alerts, validation, and deployment-owned staging drills.

The development launch profiles and AppHost seed two local-only identities with the password `Admin@123`:

- `admin@trykatch.net` — platform Administrator access.
- `tenant@trykatch.net` — Owner access to the seeded Demo Workspace.

Demo seeding and its relaxed eight-character minimum are guarded by both the Development environment and `DevelopmentDemo__Enabled`; production retains the twelve-character minimum and never enables the demo path. The configured password is reapplied to these demo identities when the development API starts so local credentials remain predictable.

## Architecture

- `src/Common` is the security kernel: identity, organizations, RBAC, PostgreSQL RLS, auditing, outbox, module validation, and shared adapters.
- `src/API` contains the HTTP host, one-shot migrator, and development Aspire AppHost.
- `src/Modules/<Module>` keeps each business capability together as Domain, Application, IntegrationEvents, Presentation, Infrastructure, and Web projects.
- `Identity` owns ASP.NET Core Identity, OpenIddict, MFA primitives, session cookies, and data-protection keys.
- `Migrator` applies ordered schema migrations, verifies the bootstrap-created runtime role, and grants it least-privilege data access.
- `Modules.Abstractions` defines the small install-time module seam and validates module identity and dependency graphs.
- `Api` owns host controllers, BFF endpoints, middleware, and explicit module composition. Module HTTP endpoints remain in their Presentation projects.
- `ServiceDefaults` owns OpenTelemetry, health, discovery, and resilient HTTP defaults.
- `AppHost` orchestrates development and tests only.
- `web/apps/web` is the React application; `web/packages/ui` is the owned component system; `web/packages/api-client` is machine-generated from OpenAPI.
- `web/packages/module-sdk` validates typed routes and navigation contributed by enabled full-stack modules.

Projects and Documents are reference organization modules; Federation is the disabled-by-default platform module. The API references only their Infrastructure entrypoints, while their React routes and navigation are generated from the same catalog. Project-reference and ArchUnitNET tests enforce layer direction and module isolation. See [module authoring](docs/modules.md).

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
dotnet restore Trykatch.slnx
dotnet build Trykatch.slnx --no-restore
dotnet test tests/Trykatch.UnitTests
dotnet test tests/Trykatch.ArchitectureTests
corepack pnpm --dir web generate
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
```

Generated files under `web/packages/api-client/src/generated` are machine-owned. Change API contracts, rebuild the API, and run `corepack pnpm --dir web generate`; never hand-edit those files.

Development API documentation is available at `/docs`, backed by the generated OpenAPI 3.1 contract at `/openapi/v1.json`. The same generation step produces the deny-by-default, provider-neutral AI tool contract in `docs/generated/assistant-contract.json`. See [AI-assisted development](docs/ai-assisted-development.md).

See [database operations](docs/database.md), [deployment/observability](docs/operations.md), and the [threat model](docs/threat-model.md) before shipping.
