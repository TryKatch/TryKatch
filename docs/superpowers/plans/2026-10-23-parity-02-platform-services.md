# TryKatch Shared Platform Services Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task. Preserve Sol/Luna ownership and the single review workflow.

**Goal:** Supply organization-safe cache/realtime/idempotency/feature/quota services and safe permission upgrades to installed modules.
**Architecture:** Narrow module-neutral contracts with host adapters. PostgreSQL owns durable business/idempotency/usage state; caches and pushes improve latency and never supply authority.
**Tech Stack:** Existing stack; SignalR and reviewed distributed cache/backplane adapters selected in P0.1, with no vendor types in module contracts.
**Spec:** [Design](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths are relative to `templates/trykatch/`; new files are proposals. S1/S3 can start after P0; S2 requires E2/E3 for current audience and committed pushes. Module enablement, feature availability, entitlement and permission remain distinct.

## Review Focus

- Same cache key in two organizations must never resolve to the same organization data.
- Permission revocation, organization switch and module disablement must invalidate live access.
- Idempotency replay cannot skip current authentication/authorization or conflict with atomic mutation responses.
- Flags cannot grant permissions or activate absent modules.
- Quota reservation/release and repeated usage events cannot inflate billing or permit capacity races.

## S1: Add scoped caching and distributed operational adapters

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Caching/OrganizationCache.cs`, `src/Common/Trykatch.Infrastructure/Caching/OrganizationCache.cs`, `CacheKeyPolicy.cs`; modify Infrastructure composition, AppHost topology and Compose adapter configuration. Add `tests/Trykatch.UnitTests/OrganizationCacheTests.cs`, `tests/Trykatch.IntegrationTests/OrganizationCacheIntegrationTests.cs`.

**Interfaces:** Implement the spec's `IOrganizationCache` with a typed key descriptor distinguishing organization-wide and current-actor values. Adapter TTL/invalidation and observable metrics are host policies.

- [ ] Test `EqualKeysInDifferentOrganizationsDoNotCollide`, `ActorPrivateKeyIsPartitioned`, `ConcurrentMissUsesBoundedFactoryWork`, expiry and post-commit invalidation; assert each returns only its own DTO.
- [ ] Select the existing framework cache primitive plus D5's reviewed distributed adapter. Keys include application/environment/organization/actor as required; avoid raw secrets/PII in keys. Configure local-only development behavior explicitly.
- [ ] Test two API replicas observing invalidation, disconnected cache fallback and stale authorization prevention. Never cache live EF entities or use cached permission data beyond the current security contract; invalidate affected domain DTOs after successful writes.
- [ ] Verify: unit `OrganizationCacheTests`, integration `OrganizationCacheIntegrationTests` with isolated adapter fixture; common exit. Cache failure may fall back to authoritative reads, but must not fabricate an entitlement/permission.

## S2: Add authorized realtime delivery and browser reconciliation

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Realtime/ModuleRealtimeContracts.cs`, `src/API/Trykatch.Api/Realtime/ModuleRealtimeHub.cs`, `RealtimeAudienceAuthorizer.cs`, `src/Common/Trykatch.Infrastructure/Realtime/ModuleRealtimePublisher.cs`, `web/packages/module-sdk/src/realtime.ts`; modify API composition and `web/apps/web/src/shell/AppShell.tsx` for session-aware connection lifecycle. Add unit `RealtimeAudienceTests.cs`, integration `ModuleRealtimeIntegrationTests.cs`, SDK tests and proposed `web/apps/web/e2e/realtime.spec.ts`.

**Interfaces:** Implement `IModuleRealtimePublisher.PublishAsync(ModuleRealtimeNotification notification, CancellationToken ct)`. Audience types are host-defined organization/user/resource references, not arbitrary group names. Publish small versioned invalidations or authorized message references with sequence/cursor metadata.

- [ ] Test unauthenticated connect, untrusted Origin, arbitrary group join, organization A requesting B's user/channel, access revoked after connect, workspace switch and module disable. Assert no new unauthorized event reaches the client after the current-authority check.
- [ ] Authenticate via existing same-origin cookies; protect negotiation/subscription against cross-site requests with Origin and antiforgery-aware host controls. Restore current authority for connect/subscribe/delivery, using registered resource policies for private channels.
- [ ] Deliver after durable commit using E3. Handle reconnect, duplicates and missed pushes with HTTP cursor/history reconciliation; React invalidates its generated-query keys. A backplane outage cannot make a committed message disappear from history.
- [ ] Test two replicas, session revocation, out-of-order notifications, reconnect gap and slow-client backpressure. Keep sensitive event payloads off generic groups. Provide an SSE adapter only if a selected product requires that protocol; the audience contract remains the same.
- [ ] Verify: `RealtimeAudienceTests`, `ModuleRealtimeIntegrationTests`, focused SDK/component tests and `playwright test e2e/realtime.spec.ts`; affected existing auth tests, generated clients and common exit.

## S3: Add transaction-aware mutation idempotency

**Files:** Create `src/Common/Trykatch.Modules.AspNetCore/ModuleIdempotency.cs`, `src/Common/Trykatch.Infrastructure/Idempotency/OrganizationRequestStore.cs`; modify `src/API/Trykatch.Api/Security/OrganizationTransactionMiddleware.cs`, `AtomicMutationResponse.cs` integration and role/schema profiles. Add `tests/Trykatch.UnitTests/ModuleIdempotencyTests.cs`, `tests/Trykatch.IntegrationTests/ModuleIdempotencyIntegrationTests.cs`; extend `AtomicMutationResponseIntegrationTests.cs`.

**Interfaces:** Endpoint opt-in metadata declares policy and bounded replay lifetime/response size. Durable identity is `(OrganizationId, ActorId, OperationId, Key)` plus a canonical request hash, completion status, response and expiry; host auth precedes lookup/replay.

- [ ] Test `ConcurrentIdenticalRequestsCommitOnce`, `SameKeyDifferentPayloadReturnsConflict`, actor/organization/operation separation, current authorization before replay, expiry and missing-key behavior. Assert `businessRows == 1`, `auditEffects == 1`, preserved `201/Location` and equivalent response body.
- [ ] Reserve and finalize under the same database transaction as the mutation. Stage the response with the existing atomic-response contract; never replay a success before commit. Define bounded in-progress response, rolled-back reservation recovery and a maximum key/hash/body policy in the ledger.
- [ ] Test commit failure, process crash, cancellation, response overflow, transient handler failure and stream/file endpoints. Streaming/file endpoints stay outside generic mutation replay; expired signed URLs, if later introduced, cannot outlive their own authorization lifetime.
- [ ] Verify: `ModuleIdempotencyTests`, `ModuleIdempotencyIntegrationTests`, `AtomicMutationResponseIntegrationTests` and affected `OrganizationCreationIntentTests`; common exit. Preserve organization creation's existing retry identity while adapting it deliberately, not by replacing it blindly.

## S4: Add installed-feature availability and runtime policy evaluation

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Features/OrganizationFeatureContracts.cs`, `src/Common/Trykatch.Infrastructure/Features/OrganizationFeatureEvaluator.cs`, `OrganizationFeatureStore.cs`; extend manifest/catalog diagnostics for declared feature keys, API metadata enforcement and SDK `features.ts`. Add `OrganizationFeatureTests.cs`, `OrganizationFeatureIntegrationTests.cs` and a platform settings view/stories.

**Interfaces:** Implement `IOrganizationFeatureEvaluator.IsEnabledAsync(string featureKey, CancellationToken ct)`; authoritative result combines installed/enabled module state with environment/organization rollout settings. A separate permission and entitlement decision is still required.

- [ ] Test absent/disabled modules, unknown feature keys, organization overrides, change invalidation, direct API calls and read/write kill switches. Assert enabling a flag does not add a permission or load a module.
- [ ] Add explicit operator-managed settings/audit and safe configured defaults. Expose only the current organization's evaluated availability to the browser; match navigation and backend policy.
- [ ] Test a flag changing between page render and submit, cache outage and module disablement with retained settings. A disabled flag does not delete data or retrospectively undo a committed transaction.
- [ ] Verify: `OrganizationFeatureTests`, `OrganizationFeatureIntegrationTests`, focused settings/SDK tests, generation and common exit.

## S5: Add entitlement projection, quota reservation and organization throttling

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Quotas/OrganizationQuotaContracts.cs`, `src/Common/Trykatch.Infrastructure/Quotas/OrganizationEntitlementStore.cs`, `OrganizationQuotaService.cs`, `OrganizationUsageLedger.cs`; modify endpoint rate policies, ownership/grant definitions, migrations and metrics. Add `OrganizationQuotaTests.cs`, `OrganizationQuotaIntegrationTests.cs` and scoped usage/settings UI.

**Interfaces:** Implement the spec's entitlement reader and quota reserve/commit/release methods; add a host-only entitlement publication seam for B1. Produce immutable usage records keyed by organization/resource/operation identity; aggregate them without double counting.

- [ ] Test competing reservations at a limit, retry with same operation ID, failed upload release, committed upload charge, archive versus permanent-delete accounting and duplicated usage-event delivery. Assert hard capacity never exceeds the configured reservation rule and each logical usage effect counts once.
- [ ] Implement PostgreSQL-backed atomic reservations, reconciliation/expiry for abandoned work and exact usage history. Cache only accelerates counters. Without Billing use the host-configured provider: Disabled by default; Enforced resources require explicit finite or explicit unlimited limits.
- [ ] Separate availability flags, paid entitlements and user permissions. Add per-organization request throttling with documented replica behavior, `429`/retry metadata and bounded failure behavior; quota exhaustion and transient infrastructure outage have distinct errors.
- [ ] Test no-Billing startup, unknown resources, disabled quota mode, missing enforced limits, plan downgrade while reservations exist, stale entitlement versions and loss of cache/coordination. Do not auto-delete customer data to satisfy a lower limit.
- [ ] Verify: `OrganizationQuotaTests`, `OrganizationQuotaIntegrationTests`, existing rate-limited auth/assistant/replay regression filters if middleware changes, usage UI tests and common exit.

## S6: Reconcile permissions for existing organizations

**Files:** Create `src/Common/Trykatch.Application/Organizations/InstalledPermissionReconciliation.cs`, Infrastructure `Organizations/InstalledPermissionReconciler.cs`; modify `OrganizationDirectory.cs`, the installed-catalog version ledger, bounded system-work registration and activation/upgrade diagnostics. Preserve current `OrganizationAccessResolver.cs` authority checks. Add `tests/Trykatch.UnitTests/InstalledPermissionReconciliationTests.cs`, `tests/Trykatch.IntegrationTests/InstalledPermissionReconciliationIntegrationTests.cs`.

**Interfaces:** A host-only registered operation reconciles the protected Owner role against a validated installed permission-catalog version in the current organization transaction. It records old/new catalog versions and an immutable audit result; module code/public requests cannot submit arbitrary grants. Consume E2/E4 for bounded existing-organization rollout.

- [ ] Test `OrganizationCreatedBeforeModuleEnablementGetsNewOwnerPermissions`, `ReconciliationIsIdempotent`, `NonOwnerConfiguredGrantsAreNotBroadened`, interrupted retry and concurrently changed roles. Assert only the protected Owner catalog invariant is expanded automatically; explicit non-Owner defaults remain creation-time or a separately authorized migration choice.
- [ ] Persist and reconcile catalog versions under locks/version guards before exposing a newly installed module as ready for that organization. Do not infer current Owner expansion from seeding code: existing source stores a snapshot and the resolver reads persisted rows.
- [ ] Validate removed/disabled permission definitions and re-enable behavior. Retain role history without permitting stale keys to authorize disabled module actions; preserve protected Owner/last-administrator checks and grant ceilings.
- [ ] Verify the two named test classes plus affected `PermissionCatalogTests`, `RoleValidationTests`, `AccessManagementBoundaryTests` and organization-creation regressions; common exit. Existing-organization module upgrade is not accepted until this evidence exists.

## Phase exit checklist

- [ ] All shared services and permission reconciliation have authoritative isolation/failure evidence; required external adapters are configured and observable.
- [ ] Backend-only consumers work without React; absent Billing and absent realtime/cache adapters have explicit supported behavior.
- [ ] Record evidence in proposed `docs/specs/evidence/parity-platform-services.md`.
