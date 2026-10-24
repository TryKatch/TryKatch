# S1 Scoped Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task with the existing Sol/Luna ownership. Steps use checkboxes. Execute the existing approved [S1 scope](2026-10-23-parity-02-platform-services.md#s1-add-scoped-caching-and-distributed-operational-adapters) through the Sol/Luna workflow. This document refines technical implementation choices; the frozen interface and product scope remain unchanged.

**Goal:** Cache authorized immutable read DTOs without cross-organization collisions, uncommitted publication, stale authority, or lost invalidation after a replica/cache outage.
**Architecture:** Module-neutral keys/interfaces; host HybridCache adapter; optional private Valkey L2; PostgreSQL transaction/RLS owns invalidation generations. Caches never supply authorization.
**Tech Stack:** .NET 10, existing EF/Npgsql/PostgreSQL, HybridCache and the reviewed optional Valkey adapter, MSTest/Testcontainers; existing module/CLI tooling.
**Spec:** [Section 7](../../../templates/trykatch/docs/specs/feature-parity-contracts.md#7-cache-and-realtime--s1s2), [ADR 0018](../../../templates/trykatch/docs/adr/0018-scheduler-cache-and-realtime-adapters.md), existing organization transaction and ownership contracts.
**Start condition:** The isolated worktree starts from freshly fetched develop. Dependency qualification may run independently while Phase 1 exit PR #133 qualifies. This clean branch was rebased onto the merged exit revision b70fbe49b4426e50b35c35d9ba2dc26578facc5e before product implementation. Preserve the primary checkout and prior evidence.

## Global constraints

- Preserve exact frozen module-neutral signatures, module layers, current authority, transaction-local RLS and named least-privilege roles. Do not cache authority or mutable EF objects.
- Keep default Disabled; runtime configuration is independent of template switches and module selection. No generation/OpenAPI network probe.
- Use forward migrations, explicit reserved ownership/grants and zero-skipped real PostgreSQL/adapter evidence. Preserve user work and neutral Git/PR names without attribution trailers.
- One local .NET output writer, bounded worker ownership, targeted tests, and one integrated independent code review. Existing approved scope and standing execution authorization remain in force.

## Review focus

- A factory after SaveChanges but before invalidation must never publish uncommitted data: Task 2 write-scope bypass regression.
- A previously offline peer and a late old factory must not revive stale DTOs: Tasks 3–4 generation/process cases.
- Disabled/write/re-enable and cache outage must retain durable invalidation: Tasks 2 and 4.
- RepeatableRead and current permission revocation must not select a warm value: Tasks 2–3 admission/HTTP cases.
- A cache-removal failure or missing callback after commit must not undo the business result: Task 2 interceptor and uncertain-commit cases.

## Technical decisions owned by Sol

1. A host-reserved `app.cache_invalidations` relation stores `(OrganizationId, KeyHash)` and the current opaque generation plus update time. Same-key upserts serialize and change generation in the business transaction. Every cached read obtains the committed generation through organization RLS before selecting the physical cache key. This durable watermark is the invalidation intent consumed by each peer on its next read, including after being offline. No append dispatcher, peer registry or coordinator business-data privilege is required.
2. This deliberately costs one indexed PostgreSQL lookup per cache hit. It saves expensive DTO queries/construction; it is not a database-free read path. Old factories populate only obsolete generation keys. Do not delete generation rows back to the initial sentinel while old cache entries may survive. Metadata grows with explicitly invalidated logical keys; no business-data retention/purge policy is invented here.
3. Populate/read the shared cache only in a real PostgreSQL **READ ONLY, ReadCommitted** transaction. Ordinary write/effect scopes, no transaction and unsupported isolation bypass it. A write can save, miss the cache, then roll back before invalidation; publishing that factory result would leak uncommitted data. EF tracking state and HTTP GET are insufficient admission checks.
4. Add explicit read-only organization endpoint metadata and a host `BeginReadOnlyAsync` scope method. Set transaction mode immediately after beginning it, before RLS setup SQL. Opt in only the known-safe Projects detail GET; existing scopes stay read/write. List/search remain uncached.
5. Register one scoped EF transaction interceptor/state through both control-plane and application context options. Track the underlying `DbTransaction` reference shared by enlisted wrappers. Run local eviction once after confirmed commit; discard callbacks on rollback and scope disposal. Bound/sanitize removal failures and never change a committed business result. Durable generation lookup handles an uncertain commit even when the callback did not run.
6. Persist invalidation independently of Disabled/read mode and Valkey availability. Otherwise writes while disabled can resurrect stale L2 when re-enabled. Exact CurrentActor invalidation affects only the current actor; the Projects reference uses Organization audience.
7. Derive a stable product namespace consistent between API and workers, plus environment, organization and actor where required. Do not derive the default product namespace from each executable's ApplicationName. Bound module/read-model keys to 120 characters, positive schema/policy versions, a 64-character lower-case SHA-256 parameter hash, and lower-case named TTL keys to 64 characters. Host TTL defaults are short=30 seconds and standard=120 seconds, with configured values from 1 to 600 seconds; cap serialized payload at 65,536 bytes and final physical key at 1,024 characters. No raw query terms/secrets in keys or diagnostics. Keep cached graphs detached from EF and other callers; validate type/schema identity.
8. Existing current permission/resource checks run before every cache call, including hits. Cache composition additionally requires installed module/current trusted organization context. No permission, session, token, security-stamp, key, entitlement or quota-authority cache is introduced.
9. Default mode is Disabled with authoritative reads. LocalOnly and Distributed are explicit runtime configuration, not new CLI/module-selection flags. Distributed requires static valid private configuration; generation/OpenAPI does not probe network or demand credentials. Cache outage falls back to source reads without retrying a source exception as an adapter failure.

10. Keep scoped factories alive until completion: check the caller token before entering HybridCache, pass `CancellationToken.None` to its shared operation, await it without `WaitAsync(callerToken)`, then observe the caller token again. Start a host-owned cooperative factory timeout inside the selected callback. Never abandon EF work or invoke a throwing source factory again as an adapter fallback. A canceled owner may wait for bounded cooperative work so other waiters can finish safely. The pinned HybridCache implementation releases callers after factory/L1 completion, before background L2 publication; only singleton-safe detached data may remain in that work.
11. Clear stale callback state on owner `TransactionStarted`/`TransactionStartedAsync`, covering underlying transaction-object reuse after implicit rollback. Do not clear on `TransactionUsed`, which application enlistment uses for the existing owner transaction. Qualify same-scope transaction A implicit rollback followed by transaction B commit, and caller cancellation during delayed L2 lookup/factory work.
12. Serialize invalidation per opaque logical key with the existing PostgreSQL transaction advisory-lock pattern before reading/replacing the generation. This captures the previous generation even on concurrent first insertion without relying on PostgreSQL18-specific `RETURNING OLD` syntax. Keep the four-field watermark model; insert/update policies require a nonempty UUID actor setting as well as the matching organization. The existing organization-index invariant adds `(OrganizationId, UpdatedAtUtc)`; no purge policy is introduced.

The existing independent reviewer checked these architecture choices against actual transaction seams. This design consultation is distinct from the single integrated implementation review required before PR merge.

## Task 1: Dependency qualification and module-neutral keys

**Owner:** Sol for package/image choices; a bounded Luna worker may own contract/key validation and their unit tests after interfaces are fixed.
**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Caching/OrganizationCache.cs`; host `Caching/CacheKeyPolicy.cs`, focused `OrganizationCacheTests.cs`; central package/project/lock changes owned by Sol.

**Interfaces:** `ModuleCacheAudience.Organization/CurrentActor`; `ModuleCacheKey(string ModuleId, string ReadModelKey, int SchemaVersion, int PolicyVersion, string ParametersHash, ModuleCacheAudience Audience, string TtlPolicyKey)`; `Task<T> IOrganizationCache.GetOrCreateAsync<T>(ModuleCacheKey key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken = default)` and `Task InvalidateAsync(ModuleCacheKey key, CancellationToken cancellationToken = default)`. Vendor types stay private. Task 2 fixes the internal generation/interceptor interfaces before adapter delegation.

- [ ] Qualify exact HybridCache/Microsoft StackExchangeRedis candidate graph and pinned Valkey image, permitted licenses, vulnerability audit, actual commands, expiry, same-instance factory coalescing and outage/recovery. Isolated scratch evidence does not replace the application's resolved graph audit.
- [ ] Add `ModuleCacheKey` fields: ModuleId, ReadModelKey, SchemaVersion, PolicyVersion, ParametersHash, audience Organization/CurrentActor, named TtlPolicyKey. Preserve exact frozen `IOrganizationCache.GetOrCreateAsync<T>` and `InvalidateAsync` signatures; no vendor types.
- [ ] Add failing tests for equal parameters in different organizations, actor-private partition, application/environment and schema/policy separation, invalid/bounded fields and missing/disabled module/context. Implement the smallest deterministic canonical/hash policy. Include DTO type identity checks without making invalidation generic.
- [ ] Run only directly affected contract/key/architecture tests. Confirm actual API signatures before downstream ownership begins.

## Task 2: Authoritative invalidation and safe read transactions

**Owner:** Sol; security, migrations and transaction decisions stay with the lead.
**Files:** host `Caching/` generation store/entity/transaction interceptor; `ApplicationDbContext`, `OrganizationControlPlaneDbContext` options registration; `OrganizationAdministration.cs` scope interface and `OrganizationDataScopeFactory`; `Modules.AspNetCore` read-only metadata and API `PlatformDataTransactionMiddleware`; operational resource/profile/policy declarations; new timestamped application migration/model snapshot; focused PostgreSQL and scope tests.

- [ ] Add real PostgreSQL failing cases for atomic generation/business commit and rollback, wrong organization, all runtime-role grants, and HTTP/background commit callbacks. Reserve the relation before generic module resource permissions; organization role gets SELECT/INSERT/UPDATE only, FORCE RLS, no coordinator/platform/Identity/outbox access or elevated fallback.
- [ ] Implement generation read/upsert in the current same-role organization transaction. Handle concurrent first invalidation, replacement and multiple invalidations in one transaction. Return/capture the replaced generation for exact local removal without an unbounded process map. Keep invalidation durable in Disabled mode.
- [ ] Add explicit read-only scopes/metadata and actual PostgreSQL admission checks. Cover write-scope `SaveChanges -> cache miss -> invalidation -> rollback`, no transaction and RepeatableRead bypass. Never infer safety from tracked changes.
- [ ] Implement shared scoped post-commit state/interceptor; drain once by underlying transaction reference, clean explicit/implicit rollback/disposal, and preserve committed results if eviction fails. Verify real HTTP and executor/scope commit paths, not a fake callback invocation alone.
- [ ] Apply only a forward migration through the real migrator. Update model/ownership/provisioning/inspection together; run targeted PostgreSQL cases with zero skips.

## Task 3: Read-through adapter and Projects reference

**Owner:** Bounded Luna adapter work only after Sol fixes the transaction/store interfaces; Sol integrates the actual Projects use case and sensitive behavior. Workers are not alone and must accommodate others without reverting edits; no worker children.
**Files:** Infrastructure `Caching/` options, adapter and composition; Projects `ProjectUseCases`, module manifest/version where required, key helper and focused module/host tests; generated metadata through normal tools.

- [ ] Add failing cases for same-instance miss coalescing, TTL expiry, detached DTOs/EF exclusion, oversized values, factory cancellation/source exceptions (including canceled owner with another valid waiter and delayed L2 miss), actual L2 use, old factory completing after invalidation and source fallback during cache outage.
- [ ] Implement HybridCache behind the exact host interface. No mutable EF object caching, arbitrary payload type activation or authority snapshots. Bound configuration/payload/TTL and use safe bounded metric labels. Default/unsupported-scope reads call the authoritative factory.
- [ ] Cache only Projects detail DTO after its existing permission check. Every detail-changing create/update/lifecycle operation records exact organization-audience invalidation atomically, including while Disabled. Preserve legacy/new event identity, audits and module layers. Avoid list/search wildcard invalidation.
- [ ] Prove current membership/permission revocation before a warm-cache hit through production HTTP, module disable behavior, exact mutation invalidation and rollback. Keep ordinary write/effect callers bypassed.
- [ ] Refresh manifest lock, OpenAPI/assistant metadata with actual tools; inspect drift and run focused module/reference/architecture checks.

## Task 4: Operational composition, replicas, evidence and PR

**Owner:** Sol integration/documentation; a bounded Luna may own independent configuration/tests after exact choices are fixed. One local .NET output writer at a time.
**Files:** AppHost/Compose optional adapter topology, sample configuration, safe health/metrics, operations docs and applicable French counterpart; `OrganizationCacheIntegrationTests` plus narrowly scoped process support; consolidated `parity-platform-services.md` evidence, acceptance/contract/ADR status and this plan.

- [ ] Configure optional private pinned Valkey in Aspire/Compose, without a new hosting dependency when generic AddContainer is sufficient. Static configuration validation must not perform generation-time probes. Keep absent adapters/default/backend-only behavior explicit.
- [ ] Qualify two actual process/replica caches: organization/actor isolation, a previously offline peer seeing the new committed generation, old in-flight factory, Valkey outage/recovery and Disabled/write/re-enable. Record exactly whether evidence is in-process, OS-process or production HTTP; do not promote mocks to deployment proof.
- [ ] Audit the actual application graph and permitted image/package licenses. Verify schema/runtime-role inspection and metrics/privacy; no skipped external-adapter claims.
- [ ] Use fresh actual packaged CLI renamed React/backend-only outputs for affected default/configuration/contracts and selected acceptance. Run targeted tests by default and use CI for the established cross-cutting gates.
- [ ] Inspect actual diff/results, one integrated independent review with supplemental ECC checklist in that pass, fix accepted findings with focused regressions, then open/attach PR. Wait for exact-head required CI and merge under standing authorization; continue S2 in a new clean worktree from latest develop.

## Evidence status

Planning and isolated qualification only at authoring. The isolated four-package graph audit and permitted MIT license checks pass. Actual pinned Valkey9.1.2 normal commands, same-instance 12-caller/one-factory coalescing, fresh-provider L2, outage source-factory behavior and fixed-endpoint same-client restart/recovery pass. Earlier random-port and parent pipe-buffer orchestration failures are retained as fixture evidence, not production/library defects. The actual application graph and host adapter remain unqualified. No S1 product behavior is accepted by this plan. Candidate scratch records are local under `.superpowers/sdd/2026-10-24-scoped-cache/`; authoritative evidence is written after the actual implementation and checks.

Implementation checkpoint: module-neutral keys are committed with nine focused tests; HTTP Projects detail read-only opt-in passes its real PostgreSQL regression. Background read-only scopes and shared transaction eviction hooks pass nine PostgreSQL cases after five hook cases failed before implementation; commit-state/key tests pass14. Bounded configuration passes nine focused tests. Forward migration `20261024043449_AddOrganizationCacheInvalidations` and its exact reserved grants/policies pass real migrator fresh/repeated invocation and cross-organization/missing-actor checks; the baseline model matches migrations. Eight focused schema/operational profile tests pass. Generation store, adapter, actual Projects cache use, replica/outage evidence, packaged-product checks and integrated implementation review remain incomplete; these partial checks do not accept S1.

The generation store now passes five real PostgreSQL tests after their behavioral failures: atomic business/generation commit and rollback, concurrent first insertion with a database-observed advisory lock wait, exact repeated replacement, independent organization generations, actual read-only/ReadCommitted admission and mismatched/unresolved scope rejection. Eighteen directly affected existing HTTP, audit, executor and queued-invitation checks pass with zero skips. The generation store remains internal to the host; no Hybrid adapter or Projects cache use has been wired yet.
