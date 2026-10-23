# TryKatch Execution and Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task. Follow the Sol/Luna routing and single review workflow; use checkbox steps below.

**Goal:** Make durable organization work, event effects, scheduled jobs and template email executable without bypassing the security kernel.
**Architecture:** Explicit versioned operation/consumer registry, host-owned organization execution, PostgreSQL durability and narrow coordinator grants. Keep external effects at-least-once and adapter APIs private to Infrastructure.
**Tech Stack:** Existing .NET 10/PostgreSQL stack; scheduler adapter selected by P0.1/D5.
**Spec:** [Design sections 3–4](../specs/2026-10-23-feature-parity-design.md); [roadmap/common task exit](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths below are relative to `templates/trykatch/`; new names are proposed. D1 gates E2; D5 gates scheduler adapter installation in E4. No product code exists for these proposed contracts yet.

## Review Focus

- User commands revalidate current permissions; committed-event effects use a bounded event grant with origin actor retained as provenance.
- Missing organization/actor metadata and unknown event versions fail closed; old logging-only records are not replayed into new effects.
- Each consumer commits its inbox marker with its own effect; a successful consumer never runs again because a peer failed.
- Coordinator credentials do not become business-data superuser credentials.
- Token-bearing emails stay protected at rest and never reach logs/dead-letter diagnostics in plaintext.

## E1: Define event, operation and operational ownership contracts

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Execution/ModuleExecutionContracts.cs`, `Eventing/ModuleEventContracts.cs`, `Jobs/ModuleJobContracts.cs`; modify `TrykatchModule.cs`, `TrykatchDataResourceRules.cs`, `src/Common/Trykatch.Application/Outbox/IOutboxWriter.cs`, `src/Common/Trykatch.Infrastructure/Persistence/OutboxWriter.cs`, `Organizations/RuntimeDatabaseAccessProfiles.cs`, `Organizations/HostPostgresPolicyContracts.cs`, `Organizations/PostgresSchemaContract.cs`, module-tool catalog validation and host schema migrations. Create `tests/Trykatch.UnitTests/ModuleExecutionContractTests.cs`; extend `tests/Trykatch.IntegrationTests/PostgresIsolationInspectionTests.cs`.

**Interfaces:** Produce the spec's execution request, event descriptor/consumer and scheduler records. Persist context version, organization, origin actor, operation identity, execution kind and correlation independently from arbitrary event payload. Retain compatibility with existing `OutboxEnvelope` callers through a versioned adapter/overload.

- [ ] Add `RegistryRejectsDuplicateConsumerKeysAndUnknownWorkKinds`: duplicate keys/undeclared execution policy are rejected before activation. Add `LegacyProjectAndDocumentEventNamesRemainStable`: existing serialized names are unchanged after namespace generation.
- [ ] Define exact record fields/result types in the contract ledger; add explicit host-derived scope capture at enqueue and compatible readers for old rows. Reject unknown new versions; route supported legacy rows through their explicit safe behavior.
- [ ] Declare infrastructure coordinator relations and organization-owned inbox relations, their migration/grant/policy rules and lease/dedup indexes. Extend provisioning and inspection from the same profile definitions; add no blanket schema grants.
- [ ] Test `OperationalOwnershipHasNoImplicitGrants`: the prior outbox/runtime role cannot access new relations until the explicit profile is applied; missing policies and overbroad grants fail inspection.
- [ ] Verify: `dotnet test tests/Trykatch.UnitTests --filter FullyQualifiedName~ModuleExecutionContractTests`; integration filter `FullyQualifiedName~PostgresIsolationInspectionTests`, plus existing `OutboxMigrationTests` if the envelope table changes. Complete the common task exit and commit the compatible contract/schema slice.

## E2: Implement validated background organization execution

**Files:** Create `src/Common/Trykatch.Infrastructure/Execution/OrganizationWorkExecutor.cs`, `ModuleOperationRegistry.cs`, `SystemWorkGrantValidator.cs`; add corresponding narrow Application authorization types as needed. Modify `Organizations/OrganizationAccessResolver.cs`, `OrganizationContext.cs`, composition and runtime role provisioning only where required. Reuse `OrganizationDataScopeFactory.cs`; create `tests/Trykatch.UnitTests/ModuleWorkExecutionTests.cs` and `tests/Trykatch.IntegrationTests/ModuleWorkExecutionIntegrationTests.cs`.

**Interfaces:** Consume E1. Produce `IOrganizationWorkExecutor.ExecuteAsync(ModuleExecutionRequest request, CancellationToken ct)` with typed success, denied, retryable-fault and terminal-fault outcomes; no caller-owned transaction or unchecked ambient organization context.

- [ ] Test `RevokedUserCommandDoesNotExecute`: enqueue with permission, revoke it, execute, assert `effects == 0`, terminal authorization outcome and sanitized audit. Test `CommittedEventSurvivesOriginMembershipRevocation`: bounded committed-event bookkeeping still executes, preserving origin actor attribution.
- [ ] Establish a fresh host scope, validate work registration/module enabled state, resolve current organization/placement and appropriate user/event/system authority, set transaction-local RLS values, then call the registered handler. Roll back business/audit/outbox together on failure.
- [ ] Test `SystemGrantCannotRunAnotherOperation`, `MissingContextCannotReadOrganizationRows`, `OrganizationAJobCannotMutateB`, `PooledConnectionDoesNotRetainPriorScope` and `DeniedRecipientIsNotDisclosedByEventEffect`. Use real runtime roles/raw SQL for the latter isolation assertions.
- [ ] Treat missing/revoked grants and unknown operation versions as terminal; cancellation rolls back and releases the claim for bounded retry. Do not surround external publication with EF automatic retries.
- [ ] Verify: unit filter `FullyQualifiedName~ModuleWorkExecutionTests`; integration filter `FullyQualifiedName~ModuleWorkExecutionIntegrationTests`, directly affected `PostgresRlsTests` and `OrganizationScopeMiddlewareTests`; common exit. D1 must be recorded before this task can be implemented.

## E3: Add durable per-consumer event delivery

**Files:** Create `src/Common/Trykatch.Infrastructure/Eventing/ModuleEventFanout.cs`, `ModuleEventDeliveryProcessor.cs`, `ModuleEventInbox.cs`, `ModuleEventRegistry.cs`; modify `Persistence/OutboxDelivery.cs`, `OutboxBatchProcessor.cs`, `LoggingOutboxTransport.cs`, composition and migrations. Create `tests/Trykatch.UnitTests/ModuleEventDeliveryTests.cs`, `tests/Trykatch.IntegrationTests/ModuleEventDeliveryIntegrationTests.cs`.

**Interfaces:** Consume E1/E2. Implement an explicit `IOutboxTransport` adapter that atomically records the active-consumer fan-out snapshot. Consumer identity is stable across handler implementation upgrades; payload schema version is separate. Produce durable completed/paused/retryable/terminal delivery outcomes.

- [ ] Test `TwoConsumersCommitOnceWhenSecondRetries`: first effect count remains `1`, second eventually becomes `1`, and only its unfinished delivery retries. Test crashes before fan-out commit, after fan-out commit, before effect commit and after uncertain effect acknowledgement.
- [ ] Insert the inbox marker and database effect in one organization transaction; use a unique `(OrganizationId, MessageId, ConsumerKey)` constraint and deterministic acknowledgement. Persist follow-up external delivery intent instead of sending SMTP/HTTP within the database transaction.
- [ ] Test duplicate receipt, worker competition, unknown event versions, duplicate consumer registration, organization forgery, disabled consumer pause/re-enable and no implicit history backfill. Preserve existing terminal/replay semantics and message IDs.
- [ ] Keep payloads, recipient secrets and credentials out of diagnostics. Add bounded backlog/oldest-age/retry/terminal metrics and sanitized operator queries through an explicit platform permission.
- [ ] Verify: unit `ModuleEventDeliveryTests`; integration `ModuleEventDeliveryIntegrationTests` plus existing `OutboxRecoveryIntegrationTests` and `AuditIntentAtomicityTests` when their transaction paths change; common exit.

## E4: Implement queued, delayed and recurring jobs

**Files:** Create `src/Common/Trykatch.Infrastructure/Jobs/PostgresModuleJobScheduler.cs`, `ModuleJobProcessor.cs`, `ModuleScheduleRegistry.cs`; adapter-specific files follow D5. Modify composition, API AppHost and runtime grants/migrations as recorded in E1. Create `tests/Trykatch.UnitTests/ModuleJobSchedulerTests.cs`, `tests/Trykatch.IntegrationTests/ModuleJobSchedulerIntegrationTests.cs`, proposed `src/API/Trykatch.Api/Controllers/ModuleWorkController.cs` and a platform operations view through `web/apps/web/src/views/ModuleWorkPage.tsx`.

**Interfaces:** Implement E1's enqueue/schedule contract and explicit recurring registration with stable module/job IDs and UTC due times. Consume E2 for every business execution. Return durable work IDs; retries keep the logical identity.

- [ ] Test immediate/delayed execution, no early execution, duplicate recurring registration, two-worker competition, restart after lease expiry and bounded retry exhaustion. Assert `businessEffects == 1` for one idempotent logical work item.
- [ ] Integrate the selected maintained adapter or documented worker extension behind these contracts. If external queue storage cannot join the domain transaction, enqueue via the existing outbox/transactional work intent; never acknowledge a business commit before its work intent is durable.
- [ ] Test `DisabledModulePausesQueuedAndRecurringWork`, `RevokedActorJobIsTerminal` and `CancelledLeaseDoesNotLoseWork`. Re-enable resumes compatible queued versions; removed handler versions surface as visible terminal incompatibility.
- [ ] Add platform-only inspection, pause/retry controls and audit. Job names/destinations/payload types are allowlisted; UI cannot invoke arbitrary serialized methods. Add health, telemetry, shutdown bounds and an operator recovery runbook.
- [ ] Verify: unit `ModuleJobSchedulerTests`; integration `ModuleJobSchedulerIntegrationTests` and affected module-activation checks; targeted platform component/browser tests, OpenAPI/client generation and common exit.

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
- [ ] Record evidence in proposed `docs/specs/evidence/parity-execution.md`; link it from the coverage ledger.
