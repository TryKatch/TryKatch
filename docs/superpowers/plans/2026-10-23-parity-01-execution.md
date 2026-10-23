# TryKatch Execution and Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task. Follow the Sol/Luna routing and single review workflow; use checkbox steps below.

**Goal:** Make durable organization work, event effects, scheduled jobs and template email executable without bypassing the security kernel.
**Architecture:** Explicit versioned operation/consumer registry, host-owned organization execution, PostgreSQL durability and narrow coordinator grants. Keep external effects at-least-once and adapter APIs private to Infrastructure.
**Tech Stack:** Existing .NET 10/PostgreSQL stack; scheduler adapter selected by P0.1/D5.
**Spec:** [Design sections 3–4](../specs/2026-10-23-feature-parity-design.md); [roadmap/common task exit](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths below are relative to `templates/trykatch/`; new names are proposed. Phase 0 resolves D1 through ADR 0015 and the frozen ledger; ADR 0018 selects Quartz.NET/PostgreSQL for D5. Runtime grants/contracts and validated organization execution are implemented in E1/E2; see their evidence records. E3 implements durable per-consumer event delivery; see `templates/trykatch/docs/specs/evidence/parity-execution-e3.md` for scope, validation and the dependency-audit blocker. E4 implementation source, package pins and the additive migration (including the adapted Quartz schema) are present. The isolated Quartz/Npgsql graph audit is clean with permitted licenses; the E4 merge-gate follow-up patches the prior Pkcs, Xml and AppHost MessagePack advisories; normal restore and the solution audit now pass. Local migration, exact role/schema inspection, runtime qualification and integrated review passed; the evidence record lists the remaining release limitations. E5 queued email remains planned. Read `templates/trykatch/docs/specs/feature-parity-contracts.md` with this plan before implementation.

## Review Focus

- User commands revalidate current permissions; committed-event effects use a bounded event grant with origin actor retained as provenance.
- Missing organization/actor metadata and unknown event versions fail closed; old logging-only records are not replayed into new effects.
- Each consumer commits its inbox marker with its own effect; a successful consumer never runs again because a peer failed.
- Coordinator credentials do not become business-data superuser credentials.
- Token-bearing emails stay protected at rest and never reach logs/dead-letter diagnostics in plaintext.

## E1: Define event, operation and operational ownership contracts

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Execution/ModuleExecutionContracts.cs`, `Eventing/ModuleEventContracts.cs`, `Jobs/ModuleJobContracts.cs`; modify `TrykatchModule.cs`, `TrykatchDataResourceRules.cs`, `src/Common/Trykatch.Application/Outbox/IOutboxWriter.cs`, `src/Common/Trykatch.Infrastructure/Persistence/OutboxWriter.cs`, `Organizations/RuntimeDatabaseAccessProfiles.cs`, `Organizations/HostPostgresPolicyContracts.cs`, `Organizations/PostgresSchemaContract.cs`, module-tool catalog validation and host schema migrations. Create `tests/Trykatch.UnitTests/ModuleExecutionContractTests.cs`; extend `tests/Trykatch.IntegrationTests/PostgresIsolationInspectionTests.cs`.

**Interfaces:** Produce the spec's execution request, event descriptor/consumer and scheduler records. Persist context version, organization, origin actor, operation identity, execution kind and correlation independently from arbitrary event payload. Retain compatibility with existing `OutboxEnvelope` callers through a versioned adapter/overload.

- [x] Add `RegistryRejectsDuplicateConsumerKeysAndUnknownWorkKinds`: duplicate keys/undeclared execution policy are rejected before activation. Add `LegacyProjectAndDocumentEventNamesRemainStable`: existing serialized names are unchanged after namespace generation.
- [x] Define exact record fields/result types in the contract ledger; add explicit host-derived scope capture at enqueue and compatible readers for old rows. Reject unknown new versions; route supported legacy rows through their explicit safe behavior.
- [x] Declare infrastructure coordinator relations and organization-owned inbox relations, their migration/grant/policy rules and lease/dedup indexes. Extend provisioning and inspection from the same profile definitions; add no blanket schema grants.
- [x] Test `OperationalOwnershipHasNoImplicitGrants`: the prior outbox/runtime role cannot access new relations until the explicit profile is applied; missing policies and overbroad grants fail inspection.
- [x] Verify: `dotnet test tests/Trykatch.UnitTests --filter FullyQualifiedName~ModuleExecutionContractTests`; integration filter `FullyQualifiedName~PostgresIsolationInspectionTests`, plus existing `OutboxMigrationTests` if the envelope table changes. Complete the common task exit and commit the compatible contract/schema slice. E1 evidence records passing behavioral checks and the pre-existing dependency-audit gate failure separately; no clean standard restore/release gate is claimed.

## E2: Implement validated background organization execution

**Files:** Create `src/Common/Trykatch.Infrastructure/Execution/OrganizationWorkExecutor.cs`, `ModuleOperationRegistry.cs`, `SystemWorkGrantValidator.cs`; add corresponding narrow Application authorization types as needed. Modify `Organizations/OrganizationAccessResolver.cs`, `OrganizationContext.cs`, composition and runtime role provisioning only where required. Reuse `OrganizationDataScopeFactory.cs`; create `tests/Trykatch.UnitTests/ModuleWorkExecutionTests.cs` and `tests/Trykatch.IntegrationTests/ModuleWorkExecutionIntegrationTests.cs`.

**Interfaces:** Consume E1. Produce `IOrganizationWorkExecutor.ExecuteAsync(ModuleExecutionRequest request, CancellationToken ct)` with typed success, denied, retryable-fault and terminal-fault outcomes; no caller-owned transaction or unchecked ambient organization context.

- [x] Test `RevokedUserCommandDoesNotExecute`: enqueue with permission, revoke it, execute, assert `effects == 0`, terminal authorization outcome and sanitized audit. Test `CommittedEventSurvivesOriginMembershipRevocation`: bounded committed-event bookkeeping still executes, preserving origin actor attribution.
- [x] Establish a fresh host scope and validate trusted work registration/module state. Begin the actor-scoped control-plane transaction and set transaction-local organization/actor values before resolving current membership/access. Enlist application data in the same transaction for the work lock and matching host completion/inbox recovery under RLS; recovery acknowledges only an already committed effect. Before any new handler effect, validate current user/event/system authority inside that scope and resolve supported placement, then invoke the handler. Add `MembershipResolutionBeginsInsideActorScopedTransaction`; roll back business/audit/outbox together on failure.
- [x] Test `SystemGrantCannotRunAnotherOperation`, `MissingContextCannotReadOrganizationRows`, `OrganizationAJobCannotMutateB`, `PooledConnectionDoesNotRetainPriorScope` and `DeniedRecipientIsNotDisclosedByEventEffect`. Use real runtime roles/raw SQL for the latter isolation assertions.
- [x] Treat missing/revoked grants and unknown operation versions as terminal; cancellation rolls back and releases the claim for bounded retry. Do not surround external publication with EF automatic retries.
- [x] Verify: unit filter `FullyQualifiedName~ModuleWorkExecutionTests`; integration filter `FullyQualifiedName~ModuleWorkExecutionIntegrationTests`, directly affected `PostgresRlsTests` and `OrganizationScopeMiddlewareTests`; common exit. D1 must be recorded before this task can be implemented.

## E3: Add durable per-consumer event delivery

**Files:** E3 implements explicit consumer bindings and registry validation, transactional `ModuleEventFanout` through `Persistence/OutboxDelivery.cs`, durable delivery processing/inbox storage, existing coordinator role grants and an additive migration, and a bounded platform inspection endpoint. Focused unit and PostgreSQL integration coverage is in `tests/Trykatch.UnitTests/ModuleEventDeliveryTests.cs`, `tests/Trykatch.IntegrationTests/ModuleEventDeliveryIntegrationTests.cs` and `OutboxRecoveryIntegrationTests.cs`.

**Interfaces:** Consume E1/E2. An internal transaction-aware fan-out collaborator records active-consumer snapshots within the source outbox transaction; it does not replace the configured `IOutboxTransport` fallback for legacy/no-consumer traffic. Consumer identity is stable across handler implementation upgrades; payload schema version is separate. Produce durable completed/paused/retryable/terminal delivery outcomes.

- [x] Validate explicit installed consumer bindings, event/schema readers and trusted source provenance before fan-out or effects; preserve legacy logging/custom transport behavior and do not backfill previously processed history.
- [x] Persist active-consumer snapshots with source `ProcessedAt` in one outbox-role transaction. Database failures roll back the batch; immutable duplicate snapshots are reused without resetting terminal/completed state.
- [x] Insert the inbox marker and database effect in one organization transaction; use a unique `(OrganizationId, MessageId, ConsumerKey)` constraint and deterministic acknowledgement. Persist follow-up external delivery intent instead of sending SMTP/HTTP within the database transaction.
- [x] Implement bounded coordinator claims, leases, cancellation deadline, retry budget, recovery-only inspection after the business-attempt limit, pause/resume and independent per-consumer progress.
- [x] Add bounded, permission-checked platform inspection with a closed DTO, state filter and redacted failure codes. Keep payloads, recipient secrets and credentials out of diagnostics.
- [x] Verify: unit `ModuleEventDeliveryTests`; PostgreSQL `ModuleEventDeliveryIntegrationTests`; affected `OutboxRecoveryIntegrationTests`; migration/runtime-role inspection and common exit. Record actual commands and counts in `templates/trykatch/docs/specs/evidence/parity-execution-e3.md`.

## E4: Implement queued, delayed and recurring jobs

**Files:** Create `src/Common/Trykatch.Infrastructure/Jobs/PostgresModuleJobScheduler.cs`, `ModuleJobProcessor.cs`, `ModuleScheduleRegistry.cs`; adapter-specific files follow D5. Modify composition, API AppHost and runtime grants/migrations as recorded in E1. Create `tests/Trykatch.UnitTests/ModuleJobSchedulerTests.cs`, `tests/Trykatch.IntegrationTests/ModuleJobSchedulerIntegrationTests.cs`, proposed `src/API/Trykatch.Api/Controllers/ModuleWorkController.cs` and a platform operations view through `web/apps/web/src/views/ModuleWorkPage.tsx`.

**Interfaces:** Implement E1's enqueue/schedule contract and explicit recurring registration with stable module/job IDs and UTC due times. Consume E2 for every business execution. Return durable work IDs only after the enclosing organization transaction commits; retries keep the logical identity. E4 is implemented and reviewed with local acceptance evidence; see [`module-jobs.md`](../../../templates/trykatch/docs/operations/module-jobs.md) and [E4 evidence](../../../templates/trykatch/docs/specs/evidence/parity-execution-e4.md).

- [x] Add user enqueue/delayed submission behind `IModuleJobScheduler`, using the active organization transaction, advisory-lock request scope, current membership/permission/resource-policy checks before idempotent receipt reuse, and a host-assigned WorkId. Dedup intent includes canonical payload, enqueue/schedule mode, and UTC-microsecond due time; conflicting intent is rejected. Outer transaction response staging must publish acceptance only after commit.
- [x] Add explicit host `AddModuleRecurringTarget` bindings and durable cursor reconciliation. `StartAtUtc` is inclusive. Catch-up requires explicit limits of 1–100 occurrences and at most 31 days; scans stop visibly at 100,000. Cursor advancement and occurrence insertion share a coordinator transaction; occurrence uniqueness does not include job version.
- [x] Add the opt-in Quartz 4.6.0 PostgreSQL adapter with the fixed `trykatch_work_coordinator` role, fixed work/recurring bridges, bounded forwarding/restart repair, health metrics, and no-DLL-scanning behavior. `Jobs:Enabled` defaults off; OpenAPI generation skips hosted scheduler setup. No runtime DDL or in-memory fallback; shutdown requests cooperative interruption and waits for active jobs within the host deadline.
- [x] Add bounded platform work listing and pause/resume/retry contracts under `platform.jobs.read` and `platform.jobs.manage`; access is rechecked before coordinator reads/actions. Mutation receipts use request IDs, generation checks, row locking and append-only control records. No operator permission is granted automatically.
- [x] Apply the E4 application migration (which now creates the exact 12 Quartz base tables/indexes and coordinator-only FORCE RLS policies) and verify the synchronized runtime-role provisioning and inspector expectations. Existing volumes must run the migrator and role provisioning before enabling jobs. Fresh/existing-database migrator and live schema/role inspection evidence passed locally.
- [x] Qualify restore/audit, build, focused unit and real-PostgreSQL integration behavior, OpenAPI/client generation, targeted web checks, restart repair, shutdown and recovery. Record exact commands, pass/skip counts, dependency advisories, and integrated review in [E4 evidence](../../../templates/trykatch/docs/specs/evidence/parity-execution-e4.md); the initial strict-restore advisory blocker was corrected in the merge-gate follow-up.
- [x] Keep E5 queued email and cache/realtime work out of E4.

## E5: Queue approved email templates durably

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Mailing/ModuleEmailContracts.cs`, `src/Common/Trykatch.Infrastructure/Optional/Email/QueuedEmailDelivery.cs`, `EmailDeliveryStore.cs`, `EmailTemplateRegistry.cs`; extend existing `Optional/Email/EmailModule.cs` and `SmtpOptions.cs`. Add `tests/Trykatch.UnitTests/QueuedEmailTests.cs`, `tests/Trykatch.IntegrationTests/QueuedEmailIntegrationTests.cs`, reusing `LoopbackSmtpServer.cs`.

**Interfaces:** Produce `IModuleEmailQueue.EnqueueAsync(ModuleEmailMessage message, CancellationToken ct)`; message includes a registered template/version, recipient intent, source-resource reference, registered disclosure-policy key, dedup identity and correlation. Identity-security emails use an explicit identity disclosure policy instead of fabricating organization context. Preserve existing recovery/invitation notifier interfaces through adapters.

- [ ] Test SMTP transient failure then recovery, permanent recipient/configuration failure, duplicate work, disabled email adapter and token/link expiry before dispatch. Assert a retryable send is not silently marked delivered; document potential duplicate SMTP sends after an uncertain acknowledgement.
- [ ] Add `RevokedSourceAccessSuppressesQueuedEmail` and `SourcePolicyRecheckedBeforeSmtpRetry`: remove a Chat recipient after enqueue/first transient failure, then assert no private body is sent. Validate current source/recipient disclosure authority before each attempt; missing/disabled source policy fails closed with a sanitized suppressed outcome. An already transmitted email cannot be recalled.
- [ ] Business email intents join the organization business transaction. Identity recovery/invitation intents use a reviewed identity-owned transaction/store or retain the existing path until that store exists; do not claim atomicity across unrelated EF contexts/connections.
- [ ] Protect token-bearing bodies/links at rest with a purpose-bound host protector; sanitize delivery logs and failure payloads. Queue only approved templates and verified application/organization brand values. In email-disabled products return an explicit unavailable/no-delivery outcome while existing copy-link recovery remains usable.
- [ ] Add template previews, delivery state and correlated metrics; retain synchronous adapter compatibility during migration. Add new optional-source/template conditions without breaking `--email false` or backend-only output.
- [ ] Verify: `QueuedEmailTests`, `QueuedEmailIntegrationTests`, existing `SmtpTransportTests`, `InvitationEmailTests` and affected account recovery tests; common exit. No real external email is sent by qualification.

## Phase exit

- [ ] A synthetic organization operation and a real installed module event survive process restart and competing workers with correct audit and isolation evidence.
- [ ] Old event IDs, existing outbox replay and default applications remain compatible. New unconfigured capabilities fail closed or report explicit unavailability.
- [ ] Every operational relation is declared, migrated and checked under real role grants; no coordinator can bypass organization RLS.
- [ ] Record the complete Phase 1 evidence in proposed `docs/specs/evidence/parity-execution.md`; link it from the coverage ledger. E1/E2/E3 evidence is recorded separately; E4 evidence is recorded separately; E5 remains open.
