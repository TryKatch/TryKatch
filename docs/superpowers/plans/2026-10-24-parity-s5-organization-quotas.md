# Organization Quotas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task. Preserve Sol/Luna ownership, targeted tests and the single integrated review. Steps use checkbox tracking.

**Goal:** Deliver S5's host-configured entitlements, atomic reservations, immutable usage, recoverable Documents uploads, organization throttling and scoped usage UI.

**Architecture:** PostgreSQL owns capacity, operation identity and reconciliation. Modules declare resources and own business inventory/policies; the host supplies neutral contracts, exact database profiles and storage adapters. Durable admission precedes external upload I/O; final document/audit/outbox/usage state shares the existing request transaction.

**Tech Stack:** Existing .NET/EF/Npgsql, pinned AWS SDK/RustFS, local filesystem, React/TanStack/shared UI. No new infrastructure dependency or provider replacement.

**Spec:** [Platform S5](2026-10-23-parity-02-platform-services.md), [design](../specs/2026-10-23-feature-parity-design.md), template `docs/specs/feature-parity-contracts.md` section8 and ADR0015/0017. Paths below are relative to `templates/trykatch/` unless stated otherwise.

**Status:** S4 [PR #137](https://github.com/TryKatch/TryKatch/pull/137) merged as `d1c54d0` after all20 final-head CI jobs, dependency review and previews passed. The clean unpublished S5 branch was fetched and rebased onto that merge without conflicts. Six pinned storage primitive cases pass, zero skips; ADR0022 fixes the architecture. Task1 declaration tests now start implementation; S5 remains incomplete.

## Global constraints and fixed decisions

- Keep the frozen entitlement/reserve/commit/release shapes. Organization/actor come from trusted host context. Unknown resources and Enforced-but-unconfigured limits fail closed. Disabled is the default; Unlimited is explicit.
- Keep the five runtime profiles. Organization, platform, identity, outbox and coordinator credentials do not substitute for each other. No kernel reference to Billing or Documents implementation. All schema/grant/RLS/inspector changes ship together through forward migrations.
- `documents.storage-bytes` measures logical uploaded bytes across Active, Archived and pending-deletion records, plus outstanding holds. Provider versions, replication and metadata overhead are outside this metric. Fencing can release an uncommitted logical hold without purging noncurrent versions. Do not claim physical cleanup/provider capacity or delete customer data on downgrade.
- Configuration is S5's only publication source. A monotonic revision and canonical hash prevent older replicas overwriting current projection; equal revision with different content fails closed. B1 later adds a reviewed publisher through the host seam. S5 adds no mutable HTTP allowance API or commercial defaults.
- Finite admission is `committed + reserved/reconciliation-held + requested <= limit`, with checked Int64 arithmetic. Expired timestamps alone never free capacity. Preserve admitted holds through downgrade; deny further allocation when over the new limit.
- Same actor/resource/operation/units repeats Reserved as AlreadyReserved. Different actor/units conflict without disclosure. Reserve of a terminal operation conflicts; it never allocates again. Commit of Committed and Release of Released are no-ops; Commit of Released and Release of Committed conflict. Documents resolves its authorized completed upload before attempting another Reserve.
- Every initial object write uses atomic create-if-absent at a never-reused key. Fencing durably wins against finalization under the common resource lock, then installs or conditionally replaces the exact provisional object with a permanent zero-byte sentinel. Confirm that sentinel before releasing the hold. Unknown object identity/storage outcome retains the hold and retries. Never delete/reuse a sentinel key.
- This compensates durably identified never-committed work only. Committed, archived, pending-deletion and unidentified historical objects are excluded. D2 finance, D3 retention/purge and D4 delegation decisions remain user-owned.
- Default Disabled still records actual usage and durable upload identity; it does not bypass current projection checks or retry provenance. All quota-aware writers must be deployed and initial inventory completed before finite enforcement. Mixed old software writers are not qualified by two current replicas; older configuration revisions of the current software are explicitly supported.
- Use the existing module descriptor/manifest/expected-registry pattern. Retain owner/resource/unit identity with records across module absence. Disabled modules cannot allocate or execute inventory handlers; fixed host cancellation may reconcile their previously admitted provisional operations without loading module code.
- Maintenance follows ADR0015's active-organization/shared-placement eligibility. Inactive/missing/unsupported targets pause with holds retained and resume when eligible. No five-attempt terminal cutoff silently abandons capacity reconciliation.

## Review focus

- A parent transaction holding a resource lock must never wait for a child transaction acquiring that lock; publish/admit before outer finalization and compensate only after proven rollback.
- Another actor, workspace, changed payload or terminal retry must not reuse an upload/reservation or reveal the prior result.
- A late initial writer, response loss or process crash must not restore bytes after a confirmed sentinel or release capacity before finalization is excluded.
- Inventory/live charge races, archived/pending-deletion records and duplicate removal events must neither lose usage nor double-charge/refund it.
- Old configuration, module absence and unavailable coordination must not turn an enforced limit into Disabled/Unlimited or a per-replica allowance.

## Task 0: Storage qualification and architecture checkpoint

**Files:** Existing `tests/Trykatch.IntegrationTests/ObjectStorageConditionalWriteIntegrationTests.cs`; create `docs/adr/0022-organization-quotas-and-upload-fencing.md`; maintain `docs/specs/evidence/parity-storage-fencing.md`.

- [x] Qualify actual pinned conditional creation/replacement, stale-token rejection, restart, real successful-response discard/recovery, HEAD identity/length/hash/fingerprint and version-retention counterexample. Six cases pass, zero skips; `storage-conditional-six.trx`.
- [x] After S4 merge, rebase cleanly, re-read changed host interfaces, and record these fixed decisions in ADR0022. Storage ETag is an opaque conditional token, not SHA-256. Local adapter/composed crash safety remain unqualified until Tasks4/5/8.
- [x] Commit the architecture checkpoint; do not mark the quota implementation complete from raw provider tests.

## Task 1: Neutral contracts and installed resource declarations

**Files:** Create `Modules.Abstractions/Quotas/OrganizationQuotaContracts.cs`, `ModuleQuotaRegistrationRules.cs`; modify `TrykatchModule.cs`, module system-grant metadata, `tools/Trykatch.ModuleTool/ModuleWorkspace.cs`, `ModuleFacts.cs` and Documents manifest/descriptor; regenerate explicit registries. Tests: `tests/Trykatch.UnitTests/OrganizationQuotaTests.cs`, tool manifest/facts tests.

**Interfaces:** Frozen `IOrganizationEntitlementReader.GetAsync(string,CancellationToken)`, `IOrganizationQuota.ReserveAsync(string,long,Guid,CancellationToken)`, `CommitAsync(Guid,CancellationToken)` and `ReleaseAsync(Guid,CancellationToken)` return the specified records/results. Modes are Disabled/Finite/Unlimited/Unconfigured; states Reserved/Committed/Released. Add `ModuleQuotaResourceDescriptor(Key,Name,Description,Unit)` and `InstalledModuleQuotaResource(ModuleId,Resource)`, units Bytes/Count. Optional explicit versioned inventory/removal-policy references are init properties. User permission, installation and quota remain separate decisions.

- [ ] Write RED cases for owner-prefixed stable keys (maximum120), duplicate/foreign/unknown keys, invalid units, empty operation IDs, nonpositive units, negative finite limits, null-versus-Unlimited, overflow and generated-manifest drift. Match feature declaration bounds: name160, description1000, maximum100 resources per module.
- [ ] Run the smallest new unit/tool filters and inspect the actual failures.
- [ ] Add validation and manifest/facts/registry support following S4's feature pattern; retain absent resource history but expose no allocatable absent resource. Extend system grants with an explicit finite `AllowedQuotaResources` list, default empty, rather than treating business data keys as quota authority.
- [ ] Run those filters plus affected module-tool diagnostics/type checks. Verify normal/renamed/disabled/backend-only composition. Commit `feat: declare organization quota resources`.

## Task 2: Durable projection, reservations and immutable ledger

**Files:** Create Infrastructure `Quotas/QuotaRecords.cs`, `QuotaResourceLock.cs`, `OrganizationEntitlementStore.cs`, `OrganizationQuotaService.cs`, `OrganizationUsageLedger.cs`; update ApplicationDbContext, ownership/profile/RLS contracts, migrator and inspector. Tests: `OrganizationQuotaIntegrationTests`, existing exact-profile/RLS inspection filters.

**Interfaces:** A transaction-bound internal reservation engine backs the frozen public API. All mutations serialize on `quota/{organization:N}/{resource}` using one stable advisory-lock hash seed. It requires the current enlisted transaction and never commits the caller's transaction. Ledger uniqueness is organization/resource/logical-effect identity; immutable positive/negative effects aggregate once. Public Reserve marks a transaction-only effect; external object I/O must use the composite durable upload seam. Immutable effect kind prevents a missing/corrupt upload record from being mistaken for a safe storage-free expiry.

- [ ] RED: two concurrent final-slot reservations have one winner; rollback exposes no admitted state; identical retries reuse one hold; actor/units/terminal conflicts disclose no prior record; overflow/outage never admits. Test Commit/Release terminal rules and downgrade with active holds.
- [ ] Add the reserved `app.organization_entitlements`, `app.organization_quota_reservations`, `app.organization_usage` plus bounded inventory checkpoints and durable upload-operation records. Organization runtime gets exact projection SELECT, reservation/upload-state SELECT/INSERT/UPDATE and usage SELECT/INSERT; no runtime usage UPDATE/DELETE or blanket CRUD. Approved host publication uses the separate platform profile in a validated target scope. Other profiles receive no business/usage grants.
- [ ] Add FORCE RLS, tenant-first uniqueness/indexes, append-only rules and forward migrations; validate all five profiles through real PostgreSQL including missing/foreign context and raw SQL denials.
- [ ] Run the new quota filters and directly affected profile/transaction/architecture tests, nonzero with zero skips. Commit `feat: persist atomic quota reservations and usage`.

## Task 3: Host configuration publication and source authority

**Files:** Create Infrastructure `Quotas/QuotaOptions.cs`, `HostEntitlementPublisher.cs`, `QuotaPublicationAuthority.cs`; host-only publication interface in Application `Quotas/EntitlementPublication.cs`; update composition/configuration/operations docs. Tests: quota options/publication unit and PostgreSQL cases.

**Interfaces:** `IOrganizationEntitlementProjectionPublisher.PublishAsync(EntitlementPublication,CancellationToken)` is host-only and requires a validated compiled publication frame, not a caller-supplied publisher name/organization authority. Configuration registration is the only initial publisher. Publication derives target scope from current trusted organization or validated host target and uses the platform connection; it does not fabricate membership.

- [ ] RED: no-Billing/default Disabled, explicit finite/Unlimited, enforced missing limit, unknown resource, revision rollback, same-revision/different-hash conflict, concurrent publication/reservation and cache/DB loss. Old local configuration reads the newer durable projection and cannot overwrite it.
- [ ] Validate immutable startup configuration. Revision0 is default Disabled; an enforced configuration requires a positive revision. Publish a complete sorted current-organization configuration batch before the first resource lock in a scope; never open a child publisher while that parent owns a quota lock. Re-read durable projection under reservation lock.
- [ ] Preserve admitted holds through downgrade. Add append-only publication provenance with exact grants and safe source/version fields. No HTTP endpoint accepts arbitrary allowance values. B1 supplies a separately reviewed publisher later.
- [ ] Run options/publication/concurrency/profile filters and commit `feat: publish host-configured entitlements safely`.

## Task 4: Qualified conditional storage adapters

**Files:** Add neutral `Modules.Abstractions/Quotas/ConditionalObjectStorage.cs`; split focused adapters under Infrastructure `Storage/`; maintain existing IObjectStorage compatibility. Tests: local adapter unit/cross-process cases and pinned storage adapter integration cases.

**Interfaces:** `IConditionalObjectStorage.TryCreateAsync(key,content,length,contentType,identity,ct)`, `StatAsync(key,ct)` and `TryReplaceWithCancellationAsync(key,expectedVersion,identity,ct)` use typed outcomes and an opaque revision. Identity binds operation ID, original byte length/SHA-256 and metadata fingerprint. Stat separates actual current length/state from original payload metadata. Persist the adapter-derived storage namespace fingerprint with admission; a changed endpoint/bucket/root cannot silently fence a different store.

- [ ] RED: existing-object nonoverwrite, declared/actual length/hash mismatch, unowned/malformed metadata, stale replacement, cancellation, lost response, late create versus sentinel and duplicate fencing.
- [ ] Implement S3 using the qualified If-None-Match/If-Match/HEAD operations. Stream verification must bound actual bytes and hash them; a failed/unknown write is never reported committed. Existing production bucket provisioning remains external; no versioning-probe privilege is required for the logical-byte metric.
- [ ] Implement local atomic no-overwrite publication and cross-process expected-identity replacement. Serialize cooperating writers on a permanent per-key lock; keep data/identity atomically consistent, preserve legacy raw-object reads, and fail closed on lock timeout. Plain temp-file rename/check-then-overwrite is insufficient. Qualify the actual subprocess protocol, not an in-memory simulation. Local storage requires a coherent qualified filesystem/root; isolated per-replica filesystems are not a shared object store.
- [ ] Run focused adapter/cross-process/pinned-container tests, zero skips; commit `feat: fence provisional object writes`.

## Task 5: Durable Documents admission, atomic completion and inventory

**Files:** Add neutral `Quotas/ModuleUploadContracts.cs`, `ModuleQuotaInventoryContracts.cs`, `ModuleUsageRemovalContracts.cs`; host Infrastructure `Quotas/ModuleUploadAdmission.cs`, `QuotaInventoryRegistry.cs`, `QuotaInventoryReconciler.cs`; Documents Application upload fingerprint/admission integration, Domain operation identity, Infrastructure inventory adapter/manifest registration, Presentation multipart field; normal API/client regeneration. Tests: Documents use-case, quota/upload/inventory PostgreSQL and real-storage cases.

**Interfaces:** `IModuleUploadAdmission.ReserveAsync(ModuleUploadRequest,ct)` returns an immutable receipt with stable subject/key. A compiled installed upload-policy reference binds owning module, resource and required permission; the HTTP request cannot choose a permission. `FinalizeAsync<T>(receipt,Func<CancellationToken,Task<T>>,ct)` returns `ModuleUploadFinalization<T>` with Committed/AlreadyCommitted/Conflict/Unavailable disposition, value or authorized subject ID and a safe failure code. It requires the existing outer transaction, locks/revalidates admission and current authority, executes metadata work and appends usage atomically. A concurrent already-committed retry skips the callback and lets Documents load the currently authorized subject; it never repeats metadata/audit/outbox work. `CancelAsync(receipt,ct)` enters the fixed technical fencing path; it cannot target an arbitrary key.

- [ ] RED: duplicate/different file with same size, metadata mismatch, actor switch, revoked permission before finalization, legacy multipart omission and completed/archived/deleted-result retry. Canonical fingerprint covers the final normalized metadata, file identity, byte length and SHA; it is computed by module code, not accepted as HTTP authority.
- [ ] In a fresh DI scope BeginAsync → enlist → re-resolve original actor/membership/current owning permission → initialize trusted context → reserve/upload-intent/maintenance-reference commit. Return before PUT only after that child commit. Never BeginAsync on the parent scope. Allocate subject/key once; first-party retries retain the logical operation UUID, legacy callers may omit it and receive a fresh server operation.
- [ ] Register rollback compensation before PUT. Route both current raw-delete failure paths into fencing after confirmed rollback; never wait for a child resource lock while outer finalization still holds it. Unknown commit outcomes retain state for reconciliation. Final metadata/audit/outbox/usage/reservation completion is one existing outer commit.
- [ ] Add `IModuleQuotaInventorySource.ReadPageAsync(cursor,pageSize,ct)` with owned `(SourceId,OperationId,Units)` entries. Documents includes uploaded Active/Archived/pending-deletion rows, excludes metadata-only legacy records, and reports unresolved uploaded size as unavailable. Batch500 under resource locks/checkpoints; live and inventory charges deduplicate the same logical operation. Complete inventory before finite admission. While inventory is incomplete, Disabled uploads may record new effects but safe usage inspection reports initializing rather than presenting a partial total as complete. No kernel query/reference to Documents entities.
- [ ] Add a closed versioned resource-owner removal policy to `IOrganizationUsageLedger.RecordRemovalAsync(resource,sourceEffectId,units,operationId,ct)`. Missing/disabled/unconfirmed policy cannot refund. Prove negative accounting through an installed test resource with a durable confirmed-permanent-removal marker; do not add a Documents purge endpoint/policy. Archive/restore/delete-request never refund; duplicate event delivery counts once and cannot over-refund the original charge.
- [ ] Run focused upload/ledger/inventory/transaction and affected Documents architecture/API generation filters, zero skips. Commit `feat: account recoverable document uploads`.

## Task 6: Bounded host reconciliation

**Files:** Infrastructure `Quotas/QuotaMaintenanceStore.cs`, `QuotaMaintenanceWorker.cs`, `QuotaMaintenanceExecutor.cs`, `QuotaMaintenanceAuthority.cs`; OrganizationContext internal fixed host initialization; operational ownership/profiles/migration/health. Tests: quota maintenance and privilege cases.

**Interfaces:** `infrastructure.quota_maintenance_work` carries closed reservation/upload/resource references plus due/lease/status metadata only. Its closed work-kind permits transaction-only reservation expiry, provisional-upload fencing and registered resource inventory; it cannot choose a handler. Organization runtime INSERT and existing coordinator SELECT/UPDATE; no payload/key/permission set/actor override, no DELETE or coordinator business/usage access. Lease-qualified claim/finish reuses E1 patterns without inventing an installed module identity.

- [ ] RED: abandoned transaction-only hold expiry, crash before/after PUT/final commit, expired late writer, duplicate claims, malformed reference, committed-state protection, inactive organization pause/resume, module absence, unsupported placement and unavailable storage. A missing upload record cannot trigger transaction-only release.
- [ ] Validate claim against durable operation under a fresh trusted organization transaction. Establish a compiled empty-permission host principal; it cannot invoke ordinary Documents management. Originating actor is provenance only. Persist Fencing, release transaction, conditionally confirm sentinel, then release hold in a fresh transaction; acknowledge the queue afterward.
- [ ] Keep uncertain holds/retry with bounded batches/timeouts/backoff and safe health evidence; no terminal five-attempt loss. Host cancellation may finish an already admitted absent-module operation without loading module code. Pause inactive/missing organizations and unsupported placement under ADR0015.
- [ ] Run real multi-worker/restart/storage/profile filters; commit `feat: reconcile abandoned upload reservations`.

## Task 7: Distributed organization request throttling

**Files:** Infrastructure `Quotas/OrganizationRequestAdmission.cs`, API `Security/OrganizationRateLimitMiddleware.cs`, ApiHostingExtensions, exact counter-row model/migration/config/operations docs. Tests: organization rate-limit integration and affected auth/assistant/replay filters.

**Interfaces:** Fixed host policy `organization-api`; current organization comes only from resolved context. Add middleware after authorization/antiforgery and before feature/handler/replay effects. Existing earlier actor/IP/auth/assistant limits remain. PostgreSQL admission uses its own short transaction and database time, independent of business rollback.

- [ ] RED: two real API replicas share one allowance, another organization is independent, rollover, denied permissions, replayed attempts, untrusted header, cancellation and DB/cache outage.
- [ ] Use one bounded row per organization/policy with window start/count and atomic conditional upsert. Default disabled; when explicitly enabled default300 attempts/60seconds, validated limits1–1000000 and windows1–3600seconds. A successful admission counts the attempt even if business work rolls back or a feature/replay later rejects it.
- [ ] Return429 with authoritative Retry-After on exhaustion;503 on unavailable coordination, never a local fallback. Bound lock/statement waits and sanitize errors.
- [ ] Run only new limiter and directly affected middleware filters; commit `feat: throttle organization requests across replicas`.

## Task 8: Safe usage UI, qualification, review and merge

**Files:** Application/API scoped usage query/endpoint; generated clients; existing `web/apps/web/src/views/OrganizationSettingsPage.*`; operations/evidence/ADR/phase checklists; packaged acceptance scripts/tests.

**Interfaces:** Stable operation `Quotas_CurrentUsage`, permission `organizations.read` or `organizations.manage`; current org only. Return installed/retained resource name/unit, mode/version/source and committed/reserved/reconciliation-held values as Int64 strings, explicit unavailable/paused state. No keys, raw actor provenance or organization-writable allowance. Use BigInt-safe display arithmetic.

- [ ] RED UI/API tests: read-only permission, wrong organization, missing/disabled resources, outage, large Int64, zero/finite/Unlimited/over-limit and retained archived usage. Preserve unrelated Settings drafts/tabs. No implementation details in product flows except meaningful unavailable/retry state.
- [ ] Add real component stories for loading/error/permission/finite/Unlimited/over-limit, English/French, themes, keyboard and320/768/1440 layouts using shared controls. Run affected interactions/a11y, typecheck/build/fixture exclusion and an actual generated-app browser journey.
- [ ] Pack fresh renamed React and backend-only products: normal/locked restore, Release build/doctor, emitted contract parity and nonzero/no-skip PostgreSQL/storage cases. Exercise two replicas for last-slot admission, committed/failed/expired uploads, exact bytes, durable usage, throttle and organization UI; restart and absent-module/re-enable retain data/history. These are required composed proofs, not satisfied by Task0.
- [ ] Run one integrated review with the existing reviewer/ECC checklist, fix concrete findings with regressions, update evidence and open/attach a neutral PR. Exact final-head CI/dependency/previews must all pass before head-guarded merge. Start S6 from fresh merged develop; the remaining roadmap and main release stay required.

## Completion boundary

No S5 completion claim is valid until every task above has evidence at its required layer. D3 product purge/retention and B1 editable plans remain later tasks; their absence does not authorize a fake refund, physical-cleanup claim or commercial policy. Initial raw storage proofs, a green build or a single-node mock are insufficient for quota capacity/recovery/replica claims.
