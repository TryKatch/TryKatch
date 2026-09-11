# Trykatch

> Start secure. Build freely.

Trykatch is a clean-room backend foundation for .NET 10, PostgreSQL, Aspire, and the open Grafana observability stack.

## Start locally

For deployment, first read [production identity](docs/production-identity.md): three certificate/password pairs are required, and an existing plaintext key ring needs an explicit privileged dry-run/apply before the API can start.

Prerequisites: .NET SDK 10.0.301+ and Docker.

```bash
dotnet tool restore
dotnet restore Trykatch.slnx
trykatch start
```

If the Trykatch CLI is not installed, run `dotnet run --launch-profile https --project src/API/Trykatch.AppHost/Trykatch.AppHost.csproj`.

Before the first API run, apply the migrations with the migrator role as described in [docs/database.md](docs/database.md). Set `Bootstrap__PlatformAdminEmail` and `Bootstrap__PlatformAdminPassword` only for a controlled bootstrap operation, then remove them.

For Compose, copy `.env.example`, set the required immutable `TRYKATCH_RELEASE_VERSION`, and replace every secret placeholder. Protect the directly published API and deny public `/health/*` routes. The base OTLP path is private single-host plaintext; use `compose.observability-tls.yml` for authenticated TLS ingestion. See [operations](docs/operations.md) for sampling, privacy, queues, retention, alerts, validation, and staging drills.

## Architecture

- `Domain` contains framework-free organization, membership, role, invitation, audit, and Project models.
- `Application` contains focused use cases, validation, permissions, and outbound interfaces.
- `Infrastructure` owns EF Core, PostgreSQL RLS, auditing, the transactional outbox, and selected adapters.
- `Identity` owns ASP.NET Core Identity, OpenIddict, MFA primitives, session cookies, and data-protection keys.
- `Api` owns controllers, HTTP contracts, middleware, and composition.
- `ServiceDefaults` owns OpenTelemetry, health, discovery, and resilient HTTP defaults.
- `AppHost` orchestrates development and tests only.

The customer-facing word is **organization**. Tenant is used only for the shared-database isolation mechanism.

## Security invariants

- Resource routes are tenant-neutral. Scoped endpoints are marked explicitly and middleware resolves a protected server-issued workspace cookie or trusted external-client claim.
- Organization access is resolved from the authenticated actor plus protected workspace context and revalidated on every request.
- Every scoped transaction sets PostgreSQL `app.organization_id` and `app.actor_id`; RLS fails closed without them.
- Runtime database credentials cannot own tables or bypass RLS.
- External clients use OpenIddict authorization code + PKCE or client credentials. Implicit and password grants are not enabled.

See [database operations](docs/database.md) and [deployment/observability](docs/operations.md) before shipping.
