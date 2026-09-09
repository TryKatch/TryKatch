# Generated application qualification

## Purpose

Trykatch qualifies the product that a user receives from `Trykatch.Templates`, not only the canonical source tree. The acceptance runner packs the NuGet template, installs it into an isolated template hive, generates a renamed application, and starts that application from its generated production Compose definition.

This follows Microsoft's guidance to reserve infrastructure-backed integration tests for critical journeys and to exercise production-like database, filesystem, network, and container behavior through observable interfaces.

## Acceptance seam

The public interface under test is:

1. Install the packed `Trykatch.Templates` package.
2. Generate an application with `dotnet new trykatch`.
3. Start its PostgreSQL migrator, API, and React web containers.
4. Add an acceptance-only TLS listener with an ephemeral certificate.
5. Exercise browser and HTTP journeys through that generated HTTPS web origin.

The acceptance test does not reference the canonical application's assemblies or query its database directly.

## Qualified journeys

- Production certificate loading and first platform-administrator bootstrap.
- Sign-in through the generated React application.
- Final-platform-administrator protection.
- Organization provisioning and one-time invitation activation.
- Organization role creation, editing, archival, restoration, and recoverable deletion.
- Project creation, archival, restoration, and audit history.
- Cross-organization project isolation through the public HTTP interface.
- Generated module-catalog availability.

Existing PostgreSQL integration tests continue to prove the RLS policy directly. The generated-product journey proves that application middleware, authorization, and RLS compose correctly in the production container path.

## Running locally

Prerequisites are the .NET 10 SDK, Docker Compose, Node.js, pnpm 10.17.1, OpenSSL, and the Playwright Chromium browser.

```bash
pnpm --dir templates/trykatch/web install --frozen-lockfile
pnpm --dir templates/trykatch/web --filter @trykatchapp/web exec playwright install chromium
bash scripts/test-generated-application.sh
```

The runner uses an isolated temporary directory and Compose project, generates short-lived certificates, removes containers and volumes on exit, and writes each run's diagnostics to a dedicated directory under `artifacts/generated-application`. Playwright bypasses trust validation only for the acceptance-only self-signed ingress certificate; the application retains production Secure cookie policy. Set `TRYKATCH_ACCEPTANCE_KEEP_WORKSPACE=true` only while diagnosing a local failure.

## CI policy

The `verify-generated-application` job runs when backend, frontend, deployment, or packaging inputs change. Documentation-only and observability-only changes do not rebuild the generated product. Its result is included in the required `verify-template` aggregate gate, and diagnostic artifacts are retained for fourteen days even when the test fails.

## References

- [Integration tests in ASP.NET Core](https://learn.microsoft.com/aspnet/core/test/integration-tests?view=aspnetcore-10.0)
- [Containerize an app with Docker](https://learn.microsoft.com/dotnet/core/docker/build-container)
