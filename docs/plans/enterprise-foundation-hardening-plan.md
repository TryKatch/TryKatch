# Enterprise foundation hardening — technical delivery plan

Date: 2026-09-11  
Audit baseline: `452989412f48cd79985b5bc72dc8472a08e4518a`  
Status: planned; implementation and release evidence are not implied by this document.

## 1. Outcome, scope and evidence

The outcome is a reusable .NET and React template whose generated applications satisfy documented security, integrity, reliability and operational gates. The current release remains a preview. This plan addresses Astra's foundation audit and incorporates the practices examined in [the David Fowler research](../research/david-fowler-engineering-alignment.md). It does not represent endorsement or certification by Fowler, Microsoft or Apple.

Implement fixes in the canonical `templates/trykatch` source and prove them in generated applications. Include the CLI, NuGet packaging, generated workflows, production containers, frontend and English/French documentation. Historical passing tests are useful context; new findings need new regression evidence.

The existing [production readiness plan](../production-readiness-plan.md) remains the overall release policy. This document is the implementation backlog and takes precedence where its audit findings contradict earlier completion claims. The older plan's version and completed-gate statements must be reconciled in PR 14.

### Supported product boundary

- Shared PostgreSQL tenancy, enforced through organization filters and forced RLS, is the supported production candidate.
- Dedicated-database tenancy stays unavailable until separately provisioned, routed, migrated and qualified. Do not weaken RLS or reuse privileged connection strings to make it appear functional.
- Keep build-time full-stack module composition. Do not introduce runtime code loading, microservices or a new DI container during hardening.
- Use primary constructors for dependency-injected controllers and services when inheritance and initialization permit. Record justified exceptions; syntax alone is not a release criterion.
- A template update affects future generated projects. Existing applications require explicit source/configuration/migration upgrade instructions.

### Evidence vocabulary

Each item progresses through **Open → Implemented → Verified → Released**. Record PR URL, commit, test-run URL, artifact digest and published version as applicable. A merge satisfies Implemented, not automatically Verified or Released. Findings from the audit were mainly code-traced, not executed exploits; PRs must first reproduce their relevant defects through focused tests.

## 2. Delivery and review model

Create an `Enterprise foundation hardening` tracking issue during implementation. Copy the checklist below into it and link each child PR. Do not put live tokens, credentials or sensitive payloads in public issues or diagnostics.

| ID | PR / branch | Depends on | Release gate |
| --- | --- | --- | --- |
| 01 | `fix/platform-suspension-enforcement` | — | Security |
| 02 | `fix/access-management-boundaries` | 01 | Security |
| 03 | `fix/mfa-step-up-verification` | 01 | Security |
| 04 | `fix/credential-leak-boundaries` | — | Security / artifacts |
| 05 | `fix/production-identity-configuration` | 03 | Security / operations |
| 06 | `fix/administration-transaction-atomicity` | 02 | Integrity |
| 07 | `fix/outbox-database-recovery` | 04, 06 | Reliability |
| 08 | `feat/concurrency-and-bounded-queries` | 02, 06 | Integrity / capacity |
| 09 | `fix/request-and-worker-observability` | 04, 07 | Operations |
| 10 | `fix/generated-project-reproducibility` | — | Generated product |
| 11 | `ci/api-contract-and-release-gates` | 10 | Delivery |
| 12 | `fix/supported-tenant-placement` | — | Product correctness |
| 13 | `fix/frontend-production-qualification` | 03, 08 | Frontend |
| 14 | `refactor/foundation-feature-boundaries` | 08, 10, 11, 12, 13 | Maintainability / docs |
| 15 | `test/production-recovery-qualification` | 05–14 | Operational proof |
| 16 | `chore/enterprise-release-candidate` | 01–15 | Publication |

Every row starts Open. Work with no dependency may proceed independently. Integrate related security changes before beginning large structural refactors so review remains focused.

All implementation PRs target `develop`. Keep it the default branch; protect both `develop` and `main`. Delete merged short-lived branches only. Promote reviewed integration batches to `main` through PRs. No automatic stable NuGet release merely because a promotion merges.

Each PR description must include the defect/requirement, observable behavior change, compatibility/migration steps, tests, documentation and the remaining limitations. Astra reviews security-sensitive changes (01–06 and security changes in 08/13) and the integrated release candidate. Review corrections stay within their originating PR where practical.

## 3. PR specifications

### PR 01 — Platform suspension actually revokes access

**Problem:** `PlatformClaimsPrincipalFactory` emits `platform_admin` outside the suspension guard, and authorization treats that claim as universal platform authority.

**Implementation**

- Derive all platform claims from a single active-access decision. Suspended/revoked/pending users receive no effective platform administrator or platform permission claims.
- Retain valid organization membership independently; platform suspension must not imply organization deletion or lockout.
- Validate current platform access on protected requests so an old cookie cannot retain authority until the periodic security-stamp check. Security-stamp rotation remains defense in depth.
- Use the same effective-access calculation for session responses, UI capabilities and endpoint authorization. Preserve `401` for missing authentication and `403` for insufficient access.

**Tests / done:** suspend an administrator, replay their old cookie and log in afresh; all platform mutation/read endpoints deny access. An active administrator still succeeds, and the suspended platform user can use a valid tenant membership. Include permission-cache invalidation if any cache participates.

**Compatibility:** no wire-format change required. Previously tolerated suspended sessions become forbidden immediately.

### PR 02 — Central authority boundaries and final-administrator protection

**Problem:** delegated managers can manipulate higher-privileged existing accounts, including issuing activation credentials for pending administrators.

**Implementation**

- Introduce application-level platform and organization management authorization services. Inputs include authenticated actor identity, target identity, operation and proposed roles/status. Load current authority from trusted data; never accept a caller-supplied permission list as proof.
- Require the operation permission, authority over the target's existing permissions, and authority to grant the proposed permissions. Reserve administrator/Owner lifecycle operations for actors holding the corresponding protected authority, even if another role happens to contain an equivalent permission set.
- Apply this consistently to activation tokens, invitation credentials, role updates, suspension, reactivation, revocation, archive and deletion. Controllers delegate the decision; CLI/background entrypoints cannot bypass it by calling a lower-level service.
- Preserve at least one active platform Administrator and one active organization Owner. Serialize mutations under a transaction-scoped lock keyed by platform or organization; re-read relevant state after acquiring it. Apply the invariant to role-definition changes and account lifecycle operations as well as membership edits.
- Denied operations return `403`; an otherwise authorized operation that removes the last administrator returns `409` with a stable problem code. No credentials or partial writes are produced.

**Tests / done:** restricted manager versus Administrator/Owner, pending administrator activation, equivalent-permission custom roles, replacement with weaker roles, self-demotion, suspension/reactivation and two concurrent final-owner removals. Successful ordinary delegation must remain covered.

**Interfaces:** centralize operation authorization rather than adding a universal boolean `bypass` argument. Regenerate contracts for new problem codes.

### PR 03 — MFA enrollment and sensitive-action verification

**Problem:** authenticated sessions can retrieve enrolled authenticator secrets and regenerate recovery credentials without proving recent possession of an authentication factor.

**Implementation**

- Add `POST /api/v1/account/security/reauthenticate`. For local accounts verify password; when MFA is enrolled also verify a current TOTP or one-time recovery code. Use generic failures, antiforgery and existing rate-limiting/lockout conventions.
- Return an opaque, single-use grant valid for five minutes, bound to user, current session, security stamp and an allowlisted purpose. Store only its hash server-side and consume it atomically. Do not put it in URLs or browser persistent storage.
- Require the grant for enrollment start, enrolled-factor replacement/disable and recovery-code regeneration. Never return an active authenticator secret from setup.
- Store encrypted pending enrollment separately from the active factor with a ten-minute expiry. Confirm a valid pending TOTP before switching factors. Keep the old enrolled factor valid until replacement completes; reject concurrent or replayed confirmation.
- Return recovery codes once, invalidate replaced codes, rotate the security stamp and deliberately refresh or end the current session. Audit outcomes without factors, secrets or codes.
- Accounts without a local credential use an existing supported reauthentication mechanism only if it provides equivalent verified assurance. Otherwise return an explicit unsupported flow and document recovery; do not silently lower assurance.

**Interfaces:** update MFA request/response contracts and React security screens; use stable `reauthentication_required`, `grant_expired` and `enrollment_conflict` problem codes.

**Tests / done:** stolen-cookie access, wrong factor, expired/replayed/cross-session grants, concurrent consumption, enrollment abandonment, old-factor preservation and accessible English/French flows. Existing MFA enrollment must continue working after migration without exposing its key.

### PR 04 — Credential-safe proxy, persistence and packaging

**Implementation**

- Use an explicit NGINX access-log allowlist: method, status, duration and a safe location label. Omit query strings, raw URL paths, referrers, cookies and authorization values; invitation tokens occur in paths, so stripping only query strings is insufficient.
- Replace raw persisted `OutboxDelivery.LastError` exception messages with stable classified error codes and safe exception type metadata. Purge existing raw values through a documented data migration; preserve attempt counters and message state.
- Add independent exclusions for `.env` variants containing local configuration, `secrets/`, private keys and certificate bundles to NuGet packing, template generation and Docker contexts. Explicitly retain vetted `.env.example`-style templates and public trust material that the product actually needs.
- Audit package inclusion against a documented source allowlist; `.gitignore` is not an artifact boundary. Do not read or print real secret files for the tests.

**Tests / done:** build a disposable fixture containing harmless secret/certificate sentinels; inspect `.nupkg`, generated output and build-context contents. Send sentinel credentials through reset/invitation and failed transport flows and assert absence from container logs and persisted errors. Assert required public artifacts remain present.

### PR 05 — Data Protection and SMTP production configuration

**Implementation**

- Persist Data Protection keys with certificate encryption in production. Load the certificate and password through mounted/managed secret configuration. Retain old decrypting certificates during rotation; validate private-key availability and supported encryption at startup.
- Document and implement migration of existing unencrypted key records, including backup, re-encryption, restart verification and restoration. Do not delete old keys to silence configuration failures.
- Make SMTP security explicit with `StartTls` and `SslOnConnect`. Require TLS in production; retain certificate validation and make plaintext an explicit development-only choice.
- Validate enabled adapters at startup using typed options. Error messages identify missing settings without printing values.

**Tests / done:** encrypted database key material, restart/cookie compatibility, certificate rotation/restore, missing private key, invalid credentials and SMTP refusing TLS. Document deployment order before enabling stricter startup validation.

### PR 06 — Atomic business changes and audit intent

**Implementation**

- Inventory mutation paths and their participating contexts. Use an explicit application transaction boundary; complete durable writes before writing an HTTP success response. Never put retries around `await next(context)` or the whole HTTP pipeline.
- Where contexts already use the same permitted runtime role/database, enlist them on the same connection/transaction. Do not grant a broader role simply to share a transaction.
- For writes split across role boundaries, persist an immutable audit intent in the business transaction and project it to the audit store through an idempotent privileged worker. Carry event ID, actor, organization, operation and approved structured details; no raw credentials. Expose the documented eventual audit visibility and backlog alert.
- Keep transaction-local organization/actor settings established for every RLS-protected transaction. Return success only after business state and durable audit intent commit.
- Translate expected domain failures before commit. If commit outcome is uncertain, avoid falsely reporting success and use operation IDs/idempotency on retriable mutations where needed.

**Tests / done:** business failure, audit-intent failure, commit failure, disconnect, worker replay and projection failure. Verify no audit event for rolled-back state and eventual exactly-once audit projection from committed intent. Repeat under tenant runtime roles.

### PR 07 — Outbox outage recovery and safe replay

**Implementation**

- Preserve `BackgroundService`, per-batch asynchronous scopes and cancellation propagation. Catch recoverable polling/database failures outside each batch, dispose the failed scope and create a new one on retry.
- Use configurable bounded backoff with jitter (default one second up to thirty seconds), cancel waits during shutdown, and distinguish permanent configuration failures from transient outages.
- Keep bounded batches and stable message IDs. Apply a transport timeout (default thirty seconds), record safe retry state, and expose exhausted attempts as terminal failures with an authenticated, authorized replay operation. Replay retains the logical message ID and records who requested it.
- Do not wrap external publication in an automatic database execution-strategy replay. Acknowledge at-least-once delivery and require consumer deduplication; a successful publish followed by failed commit may be delivered again.
- Initially retain the existing database-lock dispatch design with bounded timeout and batch configuration. Measure its lock duration in PR 15; a lease-based redesign is a separately reviewed change if capacity targets require it.

**Tests / done:** outage at poll/query/save/commit, publish-success/commit-failure, cancellation during publication, multiple workers, exhausted retry/replay and restart. Verify no process termination for transient faults and no duplicate consumer business effects.

### PR 08 — Concurrency, invitation consumption and bounded queries

**Implementation**

- Validate and consume invitations in the same transaction using a conditional pending/unexpired state transition. Coordinate account/membership creation so failed attempts do not consume valid invitations or leave unauthorized memberships.
- Add application-managed version tokens to mutable administration and business records. Update/delete requests must include the observed version; stale writes return `409` with a stable concurrency problem code. Increment versions in conditional database writes.
- Return administration collections as `{ items, totalCount, page, pageSize }`; default page size 25, maximum 100. Apply allowlisted sorting with a unique tie-breaker and bounded search terms in the database.
- Update React tables to server pagination and show a refresh/review action on conflict. Do not silently retry a user's conflicting edit.

**Tests / done:** concurrent invitation acceptance/cancellation, replay/expiry, simultaneous record updates, stale delete, pagination stability and large-tenant queries. Add migration defaults for existing versions and update clients/OpenAPI in the same PR.

### PR 09 — Useful, privacy-safe runtime signals

**Implementation**

- Read route templates from the `RouteEndpoint` instance. Emit one completion event for success, exception and cancellation; preserve exception propagation and classify aborted responses accurately.
- Add approved error codes/fingerprints, outbox backlog age, terminal failure counts and retry outcome signals. Avoid organization/user/message IDs as metric labels.
- Document request IDs and safe debugging procedures; do not restore arbitrary exception messages to solve diagnostic gaps.
- Add actionable alert conditions for sustained database failure, backlog age and audit-projection delay. Keep dependency-specific telemetry outages from incorrectly stopping healthy business processing.

**Tests / done:** route match/miss, `401`/`403`, unhandled exception, client abort, trace correlation, bounded labels and sentinel privacy assertions. Verify the new metrics reach the configured collector and dashboards.

### PR 10 — Clean generation, installation and first CI

**Implementation**

- Include the existing generated-root pnpm pin and verify it matches the web pin. Standardize supported documentation on Corepack; preserve backend-only exclusion of frontend manifests.
- Ship a `global.json` SDK policy matching the tested major/minor and feature band, with patch roll-forward. Keep compiler/analyzer policy aligned with that SDK.
- Generate renamed NuGet dependency lockfiles through a documented first restore. Before that first restore, generated workflows must not enable caching based on files that do not exist. Later restores use locked mode after locks are generated and committed; CI enforces the documented transition.
- Pin generated action references to reviewed SHAs. Test SDK/package-manager discovery under paths with spaces and dotted names, and beneath an ancestor choosing another package manager.
- Qualify CLI generation/start, direct AppHost start, Rider and Visual Studio instructions. Prerequisite checks must identify Docker, SDK, Node/Corepack and occupied endpoints before ambiguous downstream failures.

**Tests / done:** Windows/macOS/Linux clean installation, default React and `--ui none`, first restore/build, first generated CI and startup/health. Run container qualification on supported Docker-equipped runners; record IDE checks separately rather than claiming shell tests prove IDE integration.

### PR 11 — Contract-aware CI and artifact-bound publication

**Implementation**

- Model change detection as dependencies: backend API/schema inputs affect the API/client/assistant contract gate; module changes affect matching backend/frontend registries. Test detection fixtures, including deletions and shared configuration changes.
- In one job/workspace build the API, regenerate contracts and compare tracked output. Independent checkouts must not accidentally compare old artifacts with old clients.
- Qualify the exact packages that will be released. Link artifact digests to commit and required test results; publish downloaded qualified bytes, not newly rebuilt packages.
- Require a protected-main commit and required qualification gates before release. Stable releases require explicit approval. Use minimal workflow permissions and pinned actions in both repository and generated workflows.

**Tests / done:** API-only change with stale client fails; docs-only changes avoid unrelated work; shared/kernel changes expand checks; missing/failed qualification or wrong digest prevents publication. Test release logic without publishing a test package publicly.

### PR 12 — Honest shared/dedicated tenancy contract

Remove dedicated placement as an actionable choice in UI and reject unsupported creation/update requests with a stable capability error. Existing dedicated records remain fail-closed with a clear operational explanation; do not silently reroute their data to shared storage. Keep provider interfaces documented as extension points, not a working end-to-end feature.

**Tests / done:** shared provisioning and RLS continue passing; UI/API cannot create an unusable tenant; existing unsupported records never reach a different database. Update English/French documentation and marketing claims.

### PR 13 — Frontend security and usable failure paths

Add a production CSP tested against actual bundles, explicit framing prohibition, referrer policy and an HTTPS/HSTS ingress contract. Permit only necessary script/style/connect/font sources; document any required exception and test it. Apply security headers to SPA HTML and error responses, not just API responses.

Qualify login, invitation/reset, MFA, platform roles/users, tenant members/roles, projects and documents with keyboard, focus restoration, screen-reader naming, contrast and responsive checks. Ensure pending, forbidden, unavailable-backend and concurrency states are actionable. Require English/French locale-key completeness while retaining runtime fallback for resilience.

**Tests / done:** production-container header/CSP checks; browser journeys at desktop/mobile sizes; automated accessibility plus recorded manual keyboard/screen-reader checks. No missing required translation keys or silently dropped errors.

### PR 14 — Maintainable feature seams and accurate documentation

Split concentrated host pages by feature and module-tool orchestration by responsibility without changing public module contracts. Prefer explicit DI, typed results and generated clients over duplicated DTO/URL definitions. Retain abstractions that enforce isolation or provide a real extension seam; do not introduce interfaces solely for every concrete class.

Add complete-container lifetime validation and focused analyzer/review rules for async blocking, scope capture and cancellation. Account for the migrator's intentionally isolated, disposed module provider rather than mechanically banning `BuildServiceProvider`. Preserve primary-constructor conventions and nullable/compiler rules.

Reconcile readiness/version claims and link the finding-to-PR ledger. Provide English/French install/update/uninstall/start instructions for CLI and IDEs, supported deployment modes, upgrade steps and known limitations. Identify sample modules clearly.

**Tests / done:** architecture boundaries, dependency resolution, generated clients and behavior suites pass. Documentation commands are exercised in generated projects. Refactors do not add generic mechanisms unrelated to current extension requirements.

### PR 15 — Recovery, capacity and upgrade evidence

Add reproducible staging drills for migration from the previous supported schema, failed migration recovery, backup/PITR restore, key rotation, database outage, outbox/audit recovery, collector/backend outage and graceful shutdown. Use disposable environments and recorded artifact versions.

Add versioned load profiles for authentication, tenant resolution, administration pagination and project/document writes. Record hardware/resources, dataset, tenant count, concurrent users, p50/p95/p99, error rate, throughput, pool saturation and recovery times. Include slow transports, large permitted payloads and noisy-neighbor cases.

A first benchmark produces a proposed capacity envelope and budgets. The PR cannot be marked Verified until maintainers accept the measured SLO/RTO/RPO targets and CI thresholds are configured. Numeric production commitments are not invented before hardware and workload evidence exist. Non-negotiable correctness gates remain zero cross-tenant exposure, zero false successful commits and recoverable durable work.

**Done:** repeatable reports, passing accepted budgets, restored tenant isolation, verified alert delivery and operational runbooks with ownership. Optional adapters are qualified separately; untested permutations remain labeled unsupported for production.

### PR 16 — Independent review and release candidate

Re-run the authorization, MFA, isolation, package privacy, transaction and generated-product suites on the integrated commit. Have Astra review implementations and test adequacy, including any changed assumptions from the original audit. Resolve Critical/High findings; assign owners and explicit release decisions for remaining lower-severity items.

Promote reviewed `develop` to protected `main`, qualify package bytes and publish a release candidate using the existing next-version policy. Verify installation from the public NuGet feed on clean machines, including the React workspace and CLI compatibility. Attach checksums/SBOM/provenance and update installation documentation only after the version resolves publicly.

**Done:** audit ledger links to fresh evidence, no unresolved release blockers, public-install checks pass and operational/UAT sign-off is recorded. Stable promotion is a subsequent explicit release decision after the RC succeeds.

## 4. Cross-cutting implementation rules

- Prefer framework hosting, DI, authorization and async primitives. Keep platform and organization permission domains distinct.
- Flow cancellation to cancellable I/O; dispose scopes, streams and timeout sources. Do not capture request contexts or scoped services in detached work.
- Keep external side effects outside automatically replayed database operations. Stable IDs and explicit idempotency are required where replay is possible.
- No synchronous task blocking in request/worker paths. Check any exception deliberately; a text search alone is not proof of correctness.
- Commit before success. Define expected cancellation and ambiguous commit outcomes per operation; no middleware-wide retries.
- Use bounded work: pages, batches, payloads, timeouts, retries, telemetry labels and retained diagnostic data.
- Make schema/configuration changes upgradeable. Apply additive migration before switching behavior; document rollback limits and certificate/key retention.
- Run focused tests for each defect, then integration and generated-product gates for the affected dependency graph. Reserve the full release suite for integration/release and changes that warrant it.

## 5. Audit closure checklist

- [ ] 01: suspended platform authority revoked.
- [ ] 02: target privilege boundaries and final administrators protected.
- [ ] 03: MFA secrets and sensitive operations require verified assurance.
- [ ] 04: credentials excluded from logs, persistence and artifacts.
- [x] 05: production keys encrypted and mail TLS enforced.
- [ ] 06: business state and audit intent atomic before success.
- [ ] 07: outbox recovers safely with documented replay semantics.
- [ ] 08: races/concurrency controlled and queries bounded.
- [ ] 09: diagnostic routes/outcomes accurate and privacy maintained.
- [ ] 10: clean generated project and first CI reproducible.
- [ ] 11: API dependencies and exact release artifacts qualified.
- [ ] 12: only operational tenancy modes offered.
- [ ] 13: frontend security/accessibility/i18n journeys qualified.
- [ ] 14: feature seams and documentation reconciled.
- [ ] 15: accepted recovery/capacity targets proven.
- [ ] 16: independent review and public RC installation complete.

## Primary-source basis

The [Fowler comparison](../research/david-fowler-engineering-alignment.md) pins the reviewed source revisions and distinguishes explicit guidance from sample choices. Its relevant principles are async/cancellation correctness, request and DI lifetimes, bounded resource use, explicit composition and response timing. The authorization, RLS, MFA and release requirements originate in our audit and product threat model; his sample repositories do not certify those controls.

- [Fowler async guidance](https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/bcc7ca394eeb7113da6b3071f738ee5361b67db3/AsyncGuidance.md)
- [Fowler ASP.NET Core guidance](https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/bcc7ca394eeb7113da6b3071f738ee5361b67db3/AspNetCoreGuidance.md)
- [TodoApp endpoint example](https://github.com/davidfowl/TodoApp/blob/307a1eadbbd77a3004c318f2377e4818bc400af6/Todo.Api/Todos/TodoApi.cs)
