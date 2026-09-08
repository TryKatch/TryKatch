# Flatpack production-readiness plan

## Objective

Move Flatpack from a production-oriented pre-release template to a stable template that teams can safely adopt for production workloads. Until every required gate below passes, packages use a prerelease version and are not promoted as production-ready.

## Current baseline

- The canonical .NET solution builds with zero warnings and errors.
- Unit, PostgreSQL/Testcontainers integration, web, and generated-client tests pass locally.
- PostgreSQL row-level security blocks cross-organization access for the runtime role.
- Tenant provisioning, first-owner invitations, neutral workspace URLs, platform separation, platform roles, organization roles, and template generation have passed focused UAT.
- `Flatpack.Templates` can be packed, installed with `dotnet new install`, generated with renamed solutions, and discovered by Rider and Visual Studio through the .NET template engine.
- OpenAPI 3.1 now drives the generated React client, development-only Scalar reference, module ownership metadata, and a deny-by-default assistant tool contract with drift checks.

## Delivery plan

### 0. Repository and release governance

- Keep the GitHub repository private during pre-release development.
- Use `develop` as the default integration branch and `main` as the protected release branch.
- Merge short-lived feature and fix branches into `develop`; automatically delete them after merge.
- Run CI for pull requests and pushes to `develop` and `main`.
- Require reviewed promotion from `develop` to `main` before creating a version tag.
- Permit the release workflow to publish only tags whose commit is contained in `main`.
- Configure the NuGet API key only when the package is approved for publication.

Exit criteria: branch policy is documented and enforced; CI succeeds on both long-lived branches; release tags outside `main` are rejected.

### 1. Product-data and local-development correctness

- Replace every hard-coded dashboard count, activity item, and health value with an API-backed query or an explicitly labeled sample state.
- Align the Vite fallback proxy URL with the API launch profile and cover it with a local smoke test.
- Ensure loading, empty, signed-out, forbidden, degraded-backend, and retry states are deliberate and accessible.
- Verify every generated template option independently and in combination after these corrections.

Exit criteria: no production surface presents fabricated operational data; a fresh clone starts without undocumented environment overrides; the default and optional template matrix passes.

### 2. Clean generated-application qualification

- Pack the template, install it into an isolated template hive, and generate a new application using a dotted and hyphenated name.
- Restore, build, test, migrate, start, and exercise that generated application independently from the canonical source tree.
- Verify default React output, `--ui none`, every optional module, and all modules together.
- Confirm OpenAPI, the generated TypeScript client, and the generated assistant contract have no drift.

Exit criteria: the generated application—not only the template source—passes the complete automated and browser UAT suite with zero warnings.

### 3. Production container and network hardening

- Build the API and web images from clean sources with pinned dependency versions.
- Verify non-root users, dropped Linux capabilities, read-only root filesystems, writable-volume allowlists, explicit health checks, graceful shutdown, and resource limits.
- Validate same-origin cookie/BFF behavior through the production reverse proxy.
- Pin production container images by reviewed digest and document update ownership.
- Confirm AppHost is used only for development and test orchestration.

Exit criteria: production Compose starts cleanly, health checks converge, authenticated flows work through the proxy, and container-policy tests pass.

### 4. PostgreSQL migration and recovery operations

- Validate migrator and runtime roles from a blank database and from the previous supported schema.
- Prove the runtime role cannot own tables, disable RLS, or bypass RLS.
- Define expand-and-contract migration rules, deployment ordering, rollback limits, and failure recovery.
- Automate encrypted backups and perform a timed restore drill into an isolated environment.
- Document retention, point-in-time recovery expectations, and recovery ownership.

Exit criteria: upgrade and restore drills pass with recorded recovery time and recovery point evidence; cross-organization RLS tests remain green after migration.

### 5. Observability qualification

- Start OpenTelemetry Collector, Prometheus, Loki, Tempo, and Grafana through Aspire and production Compose.
- Confirm one structured log per event, W3C trace correlation, EF Core spans, outbox spans, Prometheus scraping, Loki ingestion, Tempo ingestion, and provisioned Grafana data sources and dashboards.
- Exercise collector batching, memory limiting, retry behavior, backend unavailability, and application readiness semantics.
- Define actionable alerts and remove dashboards or alerts backed by placeholder data.

Exit criteria: automated probes can follow one request across logs and traces, validate metrics, and prove degraded telemetry does not corrupt application behavior.

### 6. Identity, authorization, and security release suite

- Re-run cookie, CSRF, lockout, email verification, TOTP, one-time recovery code, security-stamp invalidation, invitation, Authorization Code + PKCE, client-credentials, and disabled-grant scenarios.
- Verify effective permissions drive route access and control visibility for platform and organization roles.
- Test stale sessions, revoked access, final-administrator protection, invitation replay, rate limits, and organization-context tampering.
- Configure production signing and encryption certificates, persistent Data Protection keys, secret rotation, and mounted or managed secret providers.
- Run dependency vulnerability auditing, license allowlisting, SBOM generation, and secret scanning.
- Complete a focused threat-model review and remediate all release-blocking findings.

Exit criteria: the authentication and authorization matrix passes end to end; there are no unresolved high or critical security findings; no production secret is stored in source or an image.

### 7. Reliability, concurrency, and performance

- Define service-level objectives and representative tenant, membership, role, audit, and project data volumes.
- Load-test sign-in, workspace resolution, paged tables, role changes, project writes, audit reads, and outbox processing.
- Verify database connection-pool limits, transaction retries, optimistic concurrency responses, idempotency, cancellation, timeouts, and graceful degradation.
- Measure startup, readiness, latency percentiles, throughput, error rates, and resource use under sustained and burst traffic.

Exit criteria: agreed performance budgets are met without isolation failures, duplicate outbox effects, unbounded queries, or exhausted resources.

### 8. Final UAT and package promotion

- Run desktop, tablet, and mobile browser journeys with keyboard and screen-reader checks against a clean generated application.
- Confirm CRUD and recoverable lifecycle behavior for projects, memberships, invitations, organization roles, platform users, platform roles, and tenant administration.
- Record reproducible evidence for all release gates in a final UAT report.
- Publish a release candidate first and validate CLI, Rider, and Visual Studio installation on clean machines.
- Promote to a stable semantic version only after release-candidate sign-off.

Exit criteria: CI is green, final UAT is approved, installation succeeds on all supported tools, the package SBOM and checksums are attached, and the stable NuGet package is published from a tag on `main`.

## Version policy

- Current development package: `0.1.0-preview.1`.
- Preview versions may be shared for evaluation but are not represented as production-ready.
- Release-candidate versions begin only after phases 1–7 pass.
- The first stable version is published only after phase 8 sign-off.

## Definition of production-ready

Flatpack is production-ready when a newly generated application passes the complete release pipeline from a clean environment, all isolation and security controls are proven, operational recovery is rehearsed, production telemetry is verified end to end, performance budgets are met, and the signed-off package is reproducibly published from `main`.
