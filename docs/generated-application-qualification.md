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

The browser tests use the generated application's public UI and HTTP interfaces. The isolated harness also uses its temporary PostgreSQL administrator to inject a feature-policy privilege outage and to compare retained data across module lifecycle rebuilds; this is never an application authorization path.

## Qualification journeys

- Production certificate loading and first platform-administrator bootstrap.
- Sign-in through the generated React application.
- Final-platform-administrator protection.
- Organization provisioning and one-time invitation activation.
- Organization role creation, editing, archival, restoration, and recoverable deletion.
- Project creation, archival, restoration, and audit history.
- Cross-organization project isolation through the public HTTP interface.
- Generated module-catalog availability.
- Installed feature controls, independent read/write behavior and rejection of a stale rendered write.
- Current-workspace feature availability, actual policy-store failure/recovery, and retained controls/history/business data after module removal and re-enable (feature profile).

Existing PostgreSQL integration tests continue to prove the RLS policy directly. The generated-product journey proves that application middleware, authorization, and RLS compose correctly in the production container path.

## Running locally

Prerequisites are the .NET 10 SDK, Docker Compose, Node.js, pnpm 10.17.1, OpenSSL, and the Playwright Chromium browser.

```bash
pnpm --dir templates/trykatch/web install --frozen-lockfile
pnpm --dir templates/trykatch/web --filter @trykatch/web exec playwright install chromium
bash scripts/test-generated-application.sh
```

The runner uses an isolated temporary directory and Compose project, generates short-lived certificates, removes containers and volumes on exit, and writes each run's diagnostics to a dedicated directory under `artifacts/generated-application`. Playwright bypasses trust validation only for the acceptance-only self-signed ingress certificate; the application retains production Secure cookie policy. Set `TRYKATCH_ACCEPTANCE_KEEP_WORKSPACE=true` only while diagnosing a local failure.

Run the feature profile with `TRYKATCH_ACCEPTANCE_FEATURES=true bash scripts/test-generated-application.sh`. It first runs the ordinary production journey, then coordinates a real policy-table SELECT privilege outage with the same running browser, verifies zero project/audit-entry/audit-intent/outbox/receipt effects during the outage, and restores the exact privilege. Authentication and platform administration remain available. It then stops the API, removes Documents and Projects through the normal CLI, rebuilds immutable hosts, verifies absence, re-enables the modules and rebuilds again. Stopped-host SQL snapshots compare business/history counts and full feature-control/history row hashes across all three states. Select the email, realtime and feature profiles separately.

Run quota acceptance separately with `TRYKATCH_ACCEPTANCE_QUOTAS=true bash scripts/test-generated-application.sh`. This mode uses two actual API containers, paired HTTPS listeners on ports 8444 and 8445, PostgreSQL, and pinned RustFS with a persistent volume. An explicit 64-byte operator allowance makes two concurrent uploads compete for the final slot. The harness verifies exact downloaded bytes, archived usage in Settings, real audit-failure rollback and cancellation objects, and data/history retention after module removal and restoration. A test-owned storage proxy withholds one successful conditional PUT response after RustFS has completed it; the harness kills that producer and observes recovery after the unchanged 15-minute reservation deadline. It also verifies that the organization request counter is shared and survives both API processes restarting. No production clock, storage result, authorization path or endpoint is replaced.

Quota acceptance can take more than fifteen minutes because it observes the real immutable expiry. Its storage proxy has focused local HTTP tests; those tests alone do not qualify RustFS or the generated application. Public identifiers, aggregate SQL observations, storage-state summaries and browser screenshots are retained; storage keys and credentials are excluded from those observations.

## CI policy

The `verify-generated-application` job runs when backend, frontend, deployment, or packaging inputs change. Documentation-only and observability-only changes do not rebuild the generated product. Its result is included in the required `verify-template` aggregate gate, and diagnostic artifacts are retained for fourteen days even when the test fails.

The renamed React and backend-only template fixtures also run normal and locked restore, Release build/doctor, and focused feature-policy PostgreSQL integration cases.

Use `TRYKATCH_TEMPLATE_PROFILE=features bash scripts/test-template.sh` for only those two renamed fixtures. The runner checks their actual generated OpenAPI/schema, installed feature facts and assistant-contract parity, and validates real test reports so a missing or skipped selection cannot pass qualification.

Use `TRYKATCH_TEMPLATE_PROFILE=quotas bash scripts/test-template.sh` for the same renamed React and backend-only products with 47 selected PostgreSQL/storage cases per product. It compares their actual emitted OpenAPI, quota declarations and regenerated assistant contract. Set `TRYKATCH_TEMPLATE_ARTIFACTS` to an absolute directory to retain each product's actual TRX, OpenAPI, module facts and assistant artifact before workspace cleanup, including failed test reports. The full template profile also runs these quota checks.

The base generated-app CI profile and the release candidate's production gate include feature qualification. Email, realtime and quotas use separate generated-app CI profiles. Release candidates must additionally pass `generated-quota-production` before package digests can be sealed. These describe required gates; a source change or a successful component test alone does not establish that a candidate passed them.

## References

- [Integration tests in ASP.NET Core](https://learn.microsoft.com/aspnet/core/test/integration-tests?view=aspnetcore-10.0)
- [Containerize an app with Docker](https://learn.microsoft.com/dotnet/core/docker/build-container)
