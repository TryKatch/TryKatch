# Trykatch production-readiness plan

## Objective

Move Trykatch from a production-oriented pre-release template to a stable template that teams can safely adopt for production workloads. Until every required gate below passes, packages use a prerelease version and are not promoted as production-ready.

## Current baseline

The coordinated release target is **Trykatch 0.1.0-preview.31**, adding organization-owned AI subscription settings and shared floating controls across host and generated forms. It retains developer onboarding, provider-neutral AI Help and Storybook. Publication requires successful exact-commit main CI and sealed tag qualification; target metadata is not proof of publication. See the [release notes](releases/0.1.0-preview.31.md).

Published **0.1.0-preview.29** introduced the branded Storybook foundation and compact floating controls. Its [qualification and publication](https://github.com/TryKatch/TryKatch/actions/runs/35318206885) succeeded and both coordinated NuGet packages became publicly available. Preview.30 addresses the subsequently reported host-form label regression; it does not close the remaining complete-catalogue or stable-readiness gates.

The published **0.1.0-preview.26** baseline added request-logging, SDK/workflow and package-delivery controls. Its [exact release-commit CI](https://github.com/TryKatch/TryKatch/actions/runs/35246520501) passed 18 jobs, 282 source unit tests and 247 PostgreSQL integration tests with zero failures/skips; [tag qualification and publication](https://github.com/TryKatch/TryKatch/actions/runs/35247873752) also passed. Its historical [release notes](releases/0.1.0-preview.26.md) accurately record that AI Help was not included.

The preceding published baseline is **Trykatch 0.1.0-preview.25**. The [2026-09-17 evidence reconciliation](enterprise-foundation-evidence.md) preserves its release commit, CI runs, shipped security fixes and remaining acceptance criteria. Eight foundation fixes were released in that baseline; the complete production-readiness gates remain open. Its release-commit CI passed 282 source unit tests and 236 PostgreSQL integration tests with zero skips. Historical records below remain dated evidence, not today's test counts or a claim of stable readiness.

- The canonical .NET solution builds with zero warnings and errors.
- Unit, PostgreSQL/Testcontainers integration, web, and generated-client tests pass locally.
- PostgreSQL row-level security blocks cross-organization access for the runtime role.
- Tenant provisioning, first-owner invitations, neutral workspace URLs, platform separation, platform roles, organization roles, and template generation have passed focused UAT.
- `Trykatch.Templates` can be packed, installed with `dotnet new install`, generated with renamed solutions, and discovered by Rider and Visual Studio through the .NET template engine.
- OpenAPI 3.1 now drives the generated React client, development-only Scalar reference, module ownership metadata, and a deny-by-default assistant tool contract with drift checks.

## Hardening evidence — 2026-09-08

The first production-hardening slice for modularity is complete:

- `trykatch.modules.json` is now the single full-stack module catalog; generated API, migrator, and React registries cannot drift independently. `trykatch.modules.lock.json` pins manifest provenance and paired package identity.
- `Trykatch.Cli` provides `module list`, `doctor`, `generate`, `enable`, and `disable`. Changes are dependency-checked, deterministic, use atomic file replacement with rollback, and preserve module files and data.
- Module manifests declare host compatibility, dependencies, capabilities, artifacts, entrypoints, permissions, routes, extension contracts, and explicitly allowlisted assistant tools.
- CI validates the module graph, backend/web parity, package shape, and generated-template matrix. Release tags package both `Trykatch.Templates` and `Trykatch.Cli` from `main`.
- Local verification passed a zero-warning Release build, 48 unit tests, 33 web/contract tests, TypeScript typecheck, Vite production build, NuGet and pnpm vulnerability audits, CLI pack/install/doctor, and all template permutations.
- Four non-container integration tests passed. Four PostgreSQL/Testcontainers tests could not execute because the local Docker daemon did not become responsive; CI remains the required authority for that gate.

This hardening does not promote Trykatch out of preview. Package acquisition/upgrade/eject/unregister, module-owned migration history and rollback, provenance/signing, container qualification, recovery drills, observability ingestion, performance budgets, and final UAT remain release gates below.

## Generated-product qualification — 2026-09-09

The clean-room qualification gate now packs and installs `Trykatch.Templates`, generates a dotted and hyphenated application name, starts the generated PostgreSQL migrator, API, and React production containers, and exercises critical platform, organization, authorization, RLS, audit, lifecycle, invitation, and module-catalog journeys through public browser and HTTP interfaces. CI retains failure diagnostics and runs this gate only when backend, frontend, deployment, or packaging inputs change.

The default React production path is covered by this gate. Backend-only and optional-adapter permutations continue to receive pack, restore, build, module-doctor, and Compose validation; runtime qualification of every optional permutation remains an exit criterion for phase 2.

## Delivery plan

The [enterprise foundation hardening plan](plans/enterprise-foundation-hardening-plan.md) translates the 2026-09-11 audit into 16 trackable PRs with dependencies, implementation requirements, regression tests and release gates. The [finding-to-PR ledger](enterprise-foundation-evidence.md) reconciles its formerly stale checklist with what has shipped and maps evidence to every production phase. Remaining acceptance criteria qualify earlier completion statements; neither unchecked historical boxes nor generic green CI establish the current state of an individual control.

The mandatory module ownership, tenant data-placement, PostgreSQL isolation, runtime-role separation, signed-module distribution, and independent reference-module work is specified in [the module and tenant data-isolation implementation plan](plans/module-data-isolation-plan.md). Its release criteria are required security gates for the phases below.

### 0. Repository and release governance

- Keep the GitHub repository private during pre-release development.
- Use `develop` as the default integration branch and `main` as the protected release branch.
- Merge short-lived feature and fix branches into `develop`; automatically delete them after merge.
- Run CI for pull requests and pushes to `develop` and `main`.
- Require reviewed promotion from `develop` to `main` before creating a version tag.
- Permit the release workflow to publish only tags whose commit is contained in `main`.
- Use NuGet trusted publishing to exchange the approved GitHub workflow identity for a temporary publishing credential; do not store a long-lived publishing API key.

Exit criteria: branch policy is documented and enforced; CI succeeds on both long-lived branches; release tags outside `main` are rejected.

2026-09-17 reconciliation: `main` is protected, but `develop` is not protected despite the hardening plan requiring both. Governance remains open until the approved protection/review policy is enforced. This documentation update does not change repository permissions.

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

- Coordinated preview target: `0.1.0-preview.31`; the preceding published package is `0.1.0-preview.30` until successful publication.
- Release metadata is prepared before tagging; this version becomes available only after the release workflow publishes successfully. Consult the GitHub release and NuGet feed, not an untagged branch, for availability.
- Preview versions may be shared for evaluation but are not represented as production-ready.
- Release-candidate versions begin only after phases 1–7 pass.
- The first stable version is published only after phase 8 sign-off.

## Definition of production-ready

Trykatch is production-ready when a newly generated application passes the complete release pipeline from a clean environment, all isolation and security controls are proven, operational recovery is rehearsed, production telemetry is verified end to end, performance budgets are met, and the signed-off package is reproducibly published from `main`.
