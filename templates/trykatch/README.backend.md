# Trykatch

Choose the customer-facing name independently of namespaces with `--display-name "Kamenta"`; see [product branding](docs/branding.md) ([Français](docs/branding.fr.md)). Skip the web configuration in this backend-only application.

> Start secure. Build freely.

Trykatch is a clean-room backend foundation for .NET 10, PostgreSQL, Aspire, and the open Grafana observability stack.

## Start locally

For a hands-on journey with checkpoints, follow [first run to first feature](docs/first-feature.md) ([Français](docs/first-feature.fr.md)). Follow the backend-only path and omit `--with-web`.

New to this application? Start with [developer onboarding](docs/developer-onboarding.md) ([Français](docs/developer-onboarding.fr.md)): architecture, backend module creation, integrations, verification and copy-paste questions for your existing coding assistant. Skip its frontend steps in this backend-only application; no separate onboarding AI key is needed.

For deployment, first read [production identity](docs/production-identity.md): three certificate/password pairs are required, and an existing plaintext key ring needs an explicit privileged dry-run/apply before the API can start.

Prerequisites: .NET SDK 10.0.301 or a later patch in the 10.0.3xx feature band, and Docker. The generated `global.json` permits patch roll-forward only. See [dependency reproducibility](docs/dependency-reproducibility.md) ([Français](docs/dependency-reproducibility.fr.md)) for first-restore and upgrade boundaries; omit frontend commands.

Open a terminal in the generated application's root directory—the folder that contains `Trykatch.slnx`, `src/`, and `tests/`—then run the commands from there:

```bash
cd /path/to/Trykatch
```

```bash
dotnet tool restore
dotnet restore Trykatch.slnx
trykatch start
```

If the Trykatch CLI is not installed, run `dotnet run --launch-profile https --project src/API/Trykatch.AppHost/Trykatch.AppHost.csproj`.

Development AppHost seeds `tenant@trykatch.net` with password `Admin@123` as Owner of Demo Workspace. These credentials are local-only; never enable demo seeding in production. Backend-only output has no generated API client: follow the [cookie/antiforgery HTTP walkthrough](docs/backend-only-http.md) ([Français](docs/backend-only-http.fr.md)) to log in, discover your membership, select the workspace and test the generated Equipment module.

Before the first API run, apply the migrations with the migrator role as described in [docs/database.md](docs/database.md). Set `Bootstrap__PlatformAdminEmail` and `Bootstrap__PlatformAdminPassword` only for a controlled bootstrap operation, then remove them.

For Compose, copy `.env.example`, set the required immutable `TRYKATCH_RELEASE_VERSION`, and replace every secret placeholder. Protect the directly published API and deny public `/health/*` routes. The base OTLP path is private single-host plaintext; use `compose.observability-tls.yml` for authenticated TLS ingestion. See [operations](docs/operations.md) for sampling, privacy, queues, retention, alerts, validation, and staging drills.

## Build with a coding agent

This application includes five project-local skills for specification, module creation, extension, review, and verification. Start with [AGENTS.md](AGENTS.md) or ask your agent to read `.agents/skills/trykatch-build-module/SKILL.md` and implement a backend feature brief. The skills detect that this application has no web workspace and skip frontend commands. See [AI-assisted development](docs/ai-assisted-development.md).

## Architecture

- `Domain` contains framework-free organization, membership, role, invitation, audit, and Project models.
- Authorized workspace managers select an active, assignable role when inviting a person; acceptance assigns the saved role. Optional SMTP emails show that role's friendly name in plain text and HTML. Without SMTP, copy the one-time invitation link from the API response.
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
