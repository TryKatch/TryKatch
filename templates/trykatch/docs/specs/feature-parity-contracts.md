# Feature parity contract ledger

Status: Phase 0 semantic baseline, with E1 contracts and E2 validated execution implemented. See [E1 evidence](evidence/parity-execution-e1.md) and [E2 evidence](evidence/parity-execution-e2.md) for exact scope and validation limits; E3–E5 remain planned.

Baseline: `origin/develop` at `d3e8dc40b6c5d4d20a173ec2f884353f4f25a7d9`, reconciled on 2026-10-23. The repository was fetched again before Phase 0; no upstream source changes were present. The user subsequently authorized Phase 1 beginning with E1. The source baseline above is retained for traceability.

## 1. Outcome and authority

Make the first durable module execution slice implementable without inventing identity, permissions, persistence ownership or transport semantics. This ledger also fixes the shared interface names later phases consume. New code must preserve [module boundaries](../modules.md), [data isolation](../module-data-isolation.md), the [dependency policy](../dependency-policy.md) and existing [verification rules](../development/verification.md).

Technical decisions for the parity workstream:

- [ADR 0015 — bounded background execution](../adr/0015-bounded-background-execution.md) resolves D1 for user commands, committed-event effects and scheduled system work. It does not approve the separate support-delegation UX/policy D4.
- [ADR 0016 — durable event consumption](../adr/0016-durable-module-event-consumption.md) fixes fan-out, consumer transactions, compatibility and disabled-module behavior.
- [ADR 0017 — operational data access](../adr/0017-operational-data-access-profiles.md) fixes relation ownership, role separation and transactional boundaries.
- [ADR 0018 — adapters](../adr/0018-scheduler-cache-and-realtime-adapters.md) resolves D5 at the architecture level: Quartz.NET/PostgreSQL, HybridCache/Valkey and SignalR. Package installation, resolved-graph audits and runtime qualification remain later tasks.

Business policy remains explicit: D2 billing, D3 permanent retention/scanning policy and D4 support delegation have not been decided. Dedicated placement D6, presigned/public storage D7 and cloud IaC D8 remain separate optional tracks. None blocks this documentation baseline or the synthetic first execution slice.

The [acceptance and permission matrix](feature-parity-acceptance.md) is the companion behavioral contract. If a future implementation changes a signature, type, permission or ownership rule here, update its caller plans and ADR/evidence together before merging that change. Documentation freeze does not mean an incompatible compiled contract can be shipped without a version increment.

## 2. Current source versus planned behavior

Paths in this table are application-relative and refer to existing source.

| Existing fact | Source | Required treatment |
| --- | --- | --- |
| Explicit composition and module-owned EF contributions already exist. | `src/Common/Trykatch.Modules.Abstractions/TrykatchModule.cs`, `src/Common/Trykatch.Modules.AspNetCore/TrykatchModuleEndpoints.cs` | Extend these seams; do not introduce assembly scanning or private cross-module references. |
| Runtime connections are four distinct fixed roles; access profiles are closed. | `src/Common/Trykatch.Infrastructure/Persistence/RuntimeDatabaseConnectionContract.cs`, `Organizations/RuntimeDatabaseAccessProfiles.cs`, `Organizations/RuntimeRoleProvisioner.cs` | E1 must extend provisioning, inspection and deployment configuration together. No existing grant implies access to a new `host-only` relation. |
| Organization resolution requires an actor-scoped transaction and active membership; permissions are read from persisted role grants. | `src/Common/Trykatch.Infrastructure/Organizations/OrganizationAccessResolver.cs` | User jobs must use current resolution. System/event work needs a distinct validated principal, not a fake membership. |
| New organizations seed Owner from the current permission catalog. | `src/Common/Trykatch.Infrastructure/Organizations/OrganizationDirectory.cs` | S6 must reconcile existing organizations on catalog upgrades. |
| `Role.AddMissingPermissions` exists; development demo seeding applies defaults to its demo roles. | `src/Common/Trykatch.Domain/Organizations/Role.cs`, `src/API/Trykatch.Api/Development/DevelopmentDemoSeeder.cs` | Reuse the additive primitive; demo-only repair is not a production-wide reconciliation mechanism. Do not broaden existing non-Owner roles automatically. |
| The outbox has durable failure/replay state and legacy event-name mappings. | `src/Common/Trykatch.Infrastructure/Persistence/OutboxWriter.cs`, `OutboxMessage.cs`, `OutboxDelivery.cs` | Preserve message IDs, names, attempts and terminal/replay semantics; evolve metadata compatibly. |
| The default outbox transport logs and returns success. | `src/Common/Trykatch.Infrastructure/Persistence/LoggingOutboxTransport.cs` | Add actual fan-out; do not claim that logging delivered an application effect or replay previously processed history automatically. |
| Audit intents require nonempty actor IDs and allowlisted safe details. | `src/Common/Trykatch.Application/Auditing/AuditIntent.cs` | System actors need explicit host identity/attribution support. Never put payloads/credentials into generic details. |
| Documents streams private objects and existing permission defaults are module-owned. | `src/Modules/Documents/README.md`, its Infrastructure `DocumentsModule.cs` | Preserve storage secrecy and standalone defaults; future owner-bound attachments must reauthorize every legacy route. |
| Optional exporters and storage are separate template switches. | `.template.config/template.json` | `--documents` continues to mean exporters; `--storage false` continues to disable storage adapters. |

Source inspection is evidence of current code shape, not proof that new async/runtime behavior exists. No product source, schema, package lock or runtime configuration changes in Phase 0.

## 3. Names, validation and serialization

- Module IDs stay lower-case kebab-case. New IDs are `notifications`, `webhooks`, `billing`, `catalog`, `tickets`, `chat`; existing `documents` remains unchanged.
- Operation, event, consumer, job, grant, template and policy keys are explicit stable lower-case dotted/kebab identifiers, never CLR type names from a persisted payload. Each is bounded to 120 characters unless an existing published contract imposes another limit.
- Contract and operation versions are positive integers. Identifiers are nonempty `Guid`s. Dates are UTC `DateTimeOffset`. API decimal/Int64 fields retain invariant JSON string transports.
- Payloads are immutable versioned JSON DTOs serialized with the existing web conventions and an explicitly registered reader. No arbitrary type activation, executable method serialization or assembly-qualified type lookup is permitted.
- The existing outbox one-MiB guard remains the upper ceiling; an individual descriptor may choose a smaller limit. A descriptor cannot increase the host ceiling. Store payload hashes for duplicate logical work detection.
- Cancellation rolls back uncommitted effects and leaves an unacknowledged claim recoverable. It is not silently converted into permanent success. Work/task outcomes never contain raw exception text or payload bytes.
- Contract APIs return `Task`/`Task<T>` to match existing seams. Below, `ct` is a `CancellationToken`; `JsonElement` is immutable input copied/owned past request scope, not a reference into a disposed `JsonDocument`.

## 4. Execution contract — E1/E2

### 4.1 Registration and provenance

Module-neutral descriptor types belong under `src/Common/Trykatch.Modules.Abstractions/Execution/`. Host execution/store interfaces belong in `Trykatch.Application` and implementations in `Trykatch.Infrastructure`; business modules do not reference those host projects.

| Type | Frozen fields / behavior |
| --- | --- |
| `ModuleExecutionKind` | `UserCommand`, `CommittedEvent`, `ScheduledSystem`. The host selects this from registration/provenance; JSON callers cannot supply it. |
| `ModuleOperationDescriptor` | `ModuleId`, `OperationKey`, `OperationVersion`, `ExecutionKind`, `PayloadSchemaKey`, `PayloadSchemaVersion`, `RequiredPermissionKeys`, `ResourcePolicyKey`, optional `SystemGrantKey`, `MaximumPayloadBytes`. Registration fixes accepted payload/handler and exact authority requirements. |
| `ModuleSystemGrantDescriptor` | `GrantKey`, `GrantVersion`, `OwnerModuleId`, `AuditActorId`, `AllowedOperationKeys`, `AllowedResourceKeys`. Finite explicit sets; no wildcard, arbitrary organization bypass or implicit Owner grant. |
| `ModuleExecutionIdentity` | Host-persisted `OrganizationId`, `EffectiveActorId`, optional `OriginActorId`, optional actual `MembershipId`, `ExecutionKind`, optional `GrantKey/GrantVersion`, optional source `MessageId`, `CatalogVersion`, trace correlation. A system actor is a distinct named audit principal from registration, never a fabricated ASP.NET user/membership. |
| `ModuleExecutionRequest` | Host-only work reference: `WorkId`, `ExpectedOperationKey`, `ExpectedOperationVersion`. It does **not** accept caller-provided identity, connection, permissions or payload override. |
| `ModuleExecutionOutcome` | `State`, optional safe `FailureCode`, optional `RetryAtUtc`. States: `Completed`, `AlreadyCompleted`, `Paused`, `Denied`, `RetryableFailure`, `TerminalFailure`. Cancellation can propagate before acknowledgement. |

`ModuleExecutionIdentity` is a persistence/audit shape, not an authorization token. Authorization is reconstructed at every attempt. Host-only `IOrganizationWorkExecutor.ExecuteAsync(ModuleExecutionRequest request, CancellationToken ct) -> Task<ModuleExecutionOutcome>` loads the durable work, matches the expected registration, validates trusted scope, opens the transaction and invokes the registered handler. A caller holding an arbitrary work ID cannot override its authority or invoke an unregistered operation.

Extend the host principal model explicitly for system actors; the existing `IOrganizationContext.MembershipId` must not be populated with a dummy value to satisfy a user-only guard. Existing membership-required use cases remain membership-required. `IModulePermissionAuthorizer` can authorize a validated finite system operation without pretending the principal is Owner; grant evaluation and audit attribution remain host-owned.

### 4.2 Required execution sequence

1. Claim a compatible work record through coordinator credentials; load only trusted persisted provenance and the enabled catalog registration.
2. Validate module/catalog version, work hash, organization identity and registered operation/grant. Unknown or changed identity/version is terminal; a disabled module is paused without deleting intent. Coordinator credentials never read organization business data.
3. Select the trusted effective actor from persisted user provenance or the validated system-grant registration. Use the existing host scope factory to begin a fresh actor-scoped control-plane transaction and set transaction-local `app.organization_id` and `app.actor_id` before resolving membership or organization access.
4. Inside that transaction, UserCommand resolves current membership/permissions, session/delegation constraints if applicable, and resource authority. Revoked access is denied, not endlessly retried. CommittedEvent or ScheduledSystem validates the exact registered grant and current organization/resource policy; origin user remains provenance rather than mutable execution authority. An inactive organization pauses work; denial/pause rolls back this scope.
5. Resolve supported placement through the host and enlist the application data context in the same transaction. Shared placement is the first supported mode. Expose the validated user/system principal explicitly; never fabricate `MembershipId`.
6. Invoke only the registered handler. Its database effect, inbox/dedup marker, required audit and outgoing intent commit together. External work is not performed inside that transaction.
7. Acknowledge only after durable commit; dispose scope before retry. No automatic provider retry may surround publication or another irreversible external effect.

A registry grant and origin actor alone do not authorize disclosure. Queued emails, notifications, attachments and realtime audiences independently validate their current source/recipient policy at delivery. No security check depends on a cached permission snapshot.

## 5. Events and consumer delivery — E1/E3

Module-neutral registration and context types belong under `Trykatch.Modules.Abstractions/Eventing/`. Preserve the existing `IOrganizationModuleData.Enqueue<TMessage>` caller and host `IOutboxWriter`; add compatible metadata capture behind them. Existing supported public signatures are not removed in E1.

| Type / method | Frozen fields / result |
| --- | --- |
| `IntegrationEventDescriptor` | `EventKey`, `SchemaVersion`, `OwnerModuleId`, explicit compile-time payload type/reader, `MaximumPayloadBytes`. New stable external name is independent of application namespace. |
| `ModuleEventConsumerDescriptor` | `ConsumerKey`, `OwnerModuleId`, accepted event key/version pairs, `OperationKey/OperationVersion`, `SystemGrantKey`. Consumer identity does not change merely because code is upgraded. |
| `ModuleEventContext` | `MessageId`, `OrganizationId`, `EffectiveActorId`, optional `OriginActorId`, `EventKey`, `SchemaVersion`, `OccurredAtUtc`, trace correlation. Constructed only from validated host state. |
| `IModuleEventConsumer.HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken ct)` | `Task`; handler completes its effect through the owning host transaction. Exceptions are classified by the executor; it cannot acknowledge another consumer. |
| `ModuleEventDelivery` | Coordinator record: delivery ID, source message ID, organization, consumer key, registered versions, state/lease/attempts, safe last-failure code and expected replay generation. Payload reference/hash is stable. |
| `ModuleEventInbox` | Organization-owned row uniquely keyed by `(OrganizationId, MessageId, ConsumerKey)`; committed with that consumer's effect. Schema version is recorded separately and never used to defeat deduplication. |

Fan-out atomically snapshots compatible installed consumers and persists their delivery records before the existing outbox is marked processed. The outbox acknowledgement and new records must share a transaction; E1/E3 extend the existing outbox role with exact INSERT/SELECT needs on delivery coordination, not business CRUD. Never use a separate connection/commit while claiming atomic fan-out.

One successful consumer is not executed again when another fails. Crash after business commit but before coordination acknowledgement is recovered by consulting the inbox in the next organization transaction. Follow-up email/realtime/webhook intents are persisted with the effect; network dispatch happens afterward.

Disabled consumers are paused with retained records. On re-enable, compatible work can resume. Installing a new consumer does not backfill historical events; previously processed logging-only messages stay processed. A deployment with no enabled new consumers retains the current safe logging behavior rather than silently invoking newly discovered handlers.

Legacy compatibility is explicit: retain `OutboxContractName.For` mappings for Projects/Documents and preserve existing payloads/IDs/replay generations. Nullable new context metadata does not license inventing an organization or actor for an old payload. A legacy message without safely provable scope may finish under its original logging-only adapter or become a visible compatibility outcome; it cannot produce new business side effects. Explicit export DTOs for Webhooks are a different allowlist from internal consumers and from assistant tools.

## 6. Jobs and schedules — E4

Public job submission belongs under `Trykatch.Modules.Abstractions/Jobs/`. Modules submit intent from the current trusted request context; autonomous and event-derived submission is host-only.

| Type / method | Frozen fields / result |
| --- | --- |
| `ModuleJobRequest` | `RequestId`, `JobKey`, `JobVersion`, `Payload`. RequestId is a caller retry key, not the host work identity. No actor/organization/grant/CLR method fields. |
| `ModuleJobDescriptor` | Module ID, stable job key/version, operation key/version, allowed execution kind and schema registration. All descriptors are build-time validated. |
| `ModuleWorkReceipt` | `WorkId`, `State`, optional `AcceptedAtUtc`, optional safe `FailureCode`; states `Accepted`, `Scheduled`, `AlreadyAccepted`, `Rejected`, `Unavailable`. Rejection/unavailability has no accepted timestamp; acceptance never claims a future effect ran. |
| `IModuleJobScheduler.EnqueueAsync(ModuleJobRequest request, CancellationToken ct)` | `Task<ModuleWorkReceipt>`; current user submission is UserCommand only and transactionally persists work intent. |
| `IModuleJobScheduler.ScheduleAsync(ModuleJobRequest request, DateTimeOffset dueAtUtc, CancellationToken ct)` | `Task<ModuleWorkReceipt>`; same authority/durability, UTC not-before time. |
| `ModuleRecurringScheduleDescriptor` | `ScheduleKey`, owning module, registered job key/version, UTC schedule, misfire policy `Coalesce` or explicitly bounded `CatchUp`, system grant. Registered by reviewed code, not arbitrary public cron/method input. |

The host assigns a distinct opaque globally unique WorkId. Deduplication is scoped by `(OrganizationId, trusted execution identity, JobKey, JobVersion, RequestId)`: for public submission the identity is the current authenticated actor, never caller-supplied provenance. Current authorization is checked before returning any prior receipt. Another actor or organization has its own scope and never receives the first caller's WorkId.

Within that scope, identical intent returns the original receipt. Intent equality covers canonical payload, submission mode `Enqueue` or `Schedule`, and normalized UTC due time; immediate submissions use a fixed null due time, not the retry clock. A changed payload, mode or due time under the same RequestId conflicts. Host-only event/system submission uses registered trusted provenance (for example message/consumer or schedule/occurrence), with the same isolation and intent-equality rules; modules cannot override it.

Overdue work is recoverable after restart. Lease expiry does not alter the host work identity. Retries have bounded attempt/backoff policy, sanitized failure classification and an explicit terminal/replay path.

Quartz is a host adapter, not a second business ledger. A transactionally committed Trykatch work intent is forwarded idempotently to a single registered Quartz bridge job carrying only a work reference. Do not try to share EF and Quartz transactions implicitly. Quartz callbacks ask the host executor to load authoritative scope and payload; no external invocation of arbitrary job types/methods is exposed. Recurring trigger registration uses deterministic identities; invoice catch-up/period uniqueness remains Billing domain behavior rather than scheduler magic.

## 7. Cache and realtime — S1/S2

| Contract | Frozen shape and authority |
| --- | --- |
| `ModuleCacheKey` | Module, read-model, schema version, policy version, canonical parameter hash, audience `Organization` or `CurrentActor`, named TTL policy. Host injects environment/application/current organization/actor; callers never embed raw tenant keys, slugs, query terms or credentials. |
| `IOrganizationCache.GetOrCreateAsync<T>(ModuleCacheKey key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)` | `Task<T>` of immutable authorized DTOs; caller's current operation/resource policy is enforced before reading a cached value. |
| `IOrganizationCache.InvalidateAsync(ModuleCacheKey key, CancellationToken ct)` | `Task`; immediate local eviction after commit plus durable versioned invalidation intent for peer L1s. A factory/cache hit never supplies an authorization decision. |
| `ModuleRealtimeNotification` | Module, event key/version, opaque event ID, registered audience policy and resource/user reference, safe DTO/invalidation, durable history cursor when applicable. No arbitrary group-name recipient field. |
| `IModuleRealtimePublisher.PublishAsync(ModuleRealtimeNotification notification, CancellationToken ct)` | `Task`; dispatch after source intent commits; current audience validation on each attempt, backpressure and bounded payload. |

Select HybridCache over a host interface and optional Valkey-backed L2. Read-through cache failure falls back to authoritative reads. Exclude sessions, security stamps, permissions, keys, invitation/reset/MFA tokens, mutable aggregates and sensitive disclosure decisions from cache. HybridCache L1 invalidation/stampede control is per instance; durable invalidation plus authoritative rechecks are mandatory where access changes.

Select same-origin cookie-authenticated SignalR with Origin/antiforgery-aware negotiation and validated audience policies. Database history/inbox is authoritative; a Valkey backplane is lossy and not an outbox. Multi-replica delivery requires its configured backplane and HTTP reconciliation; a broken backplane degrades live delivery without fabricating success or bypassing audience checks. No standalone SSE endpoint is required for the first parity slice.

## 8. Flags, entitlements and quotas — S4/S5/S6

| Contract | Frozen shape / result |
| --- | --- |
| `IOrganizationFeatureEvaluator.IsEnabledAsync(string featureKey, CancellationToken ct)` | `Task<bool>` for a declared installed feature under current environment/organization. Unknown/absent/disabled module feature is false; this is not permission or entitlement. |
| `OrganizationEntitlement` | `ResourceKey`, mode `Disabled/Finite/Unlimited/Unconfigured`, optional finite `Limit`, `Version`, safe `Source`, `EffectiveAtUtc`. Null alone never means both missing and unlimited. |
| `IOrganizationEntitlementReader.GetAsync(string resourceKey, CancellationToken ct)` | `Task<OrganizationEntitlement>` from host projection/configuration, independent of Billing code. |
| `ModuleQuotaReservation` | Reservation ID, resource key, positive Int64 units, operation ID, entitlement version, expiry and state. Organization/actor come from host scope. |
| `QuotaReservationResult` | `Disposition` (`Reserved`, `AlreadyReserved`, `Denied`, `Unavailable`, `Conflict`), optional `Reservation`, optional safe `FailureCode`. Only reserved states carry a reservation; duplicate same operation is idempotent, mismatched units conflict. |
| `IOrganizationQuota.ReserveAsync(string resourceKey, long units, Guid operationId, CancellationToken ct)` | `Task<QuotaReservationResult>`; no Application-layer result type leaks into module-neutral contracts. |
| `IOrganizationQuota.CommitAsync(Guid reservationId, CancellationToken ct)` / `ReleaseAsync(Guid reservationId, CancellationToken ct)` | `Task`; idempotent transitions in organization scope, with durable usage/reconciliation. No usage/refund is guessed from cache availability. |

Unknown/enforced-but-unconfigured resources fail closed. Quotas default Disabled; explicit Unlimited is separate. Without Billing, host configuration remains authoritative. Installing Billing adds a reviewed projection publisher; platform-editable plans never become writable global reference tables or a kernel reference to Billing Infrastructure.

S6 records a validated permission-catalog version and idempotently adds installed grants to the protected Owner role for an existing organization. Preserve existing non-Owner grants; creation-time module defaults are not a production upgrade mandate. Filter disabled/unknown catalog keys at runtime as the current resolver does. Make newly enabled module readiness per organization contingent on reconciliation, without blocking unrelated existing modules. Development demo repair is not sufficient evidence.

## 9. Attachments and email — F1/E5

| Type / method | Frozen shape / result |
| --- | --- |
| `ModuleAttachmentCommand` | `DocumentId`, registered `OwnerType`, `OwnerId`, logical `OperationId`; trusted scope/actor are host-derived. A document already bound to a different owner cannot silently gain a second wider audience. |
| `ModuleAttachmentReference` | Opaque attachment/document identity and authorized presentation metadata; no storage key/credential. |
| `IModuleAttachmentService.AttachAsync(ModuleAttachmentCommand command, CancellationToken ct)` | `Task<ModuleAttachmentReference>`; current owner policy and organization checks before linking. |
| `ModuleAttachmentRead` | `Content` (`Stream`), `MediaType`, `DownloadName`, optional Int64 `Length`; values come from the authoritative authorized record, not caller-provided presentation metadata. |
| `IModuleAttachmentService.OpenReadAsync(ModuleAttachmentReference reference, CancellationToken ct)` | `Task<ModuleAttachmentRead>`; current owner policy must authorize before returning Content. The caller owns asynchronous disposal of Content. |
| `IModuleAttachmentService.DetachAsync(ModuleAttachmentCommand command, CancellationToken ct)` | `Task`; owner/resource authorization; no automatic object purge. |
| `ModuleEmailMessage` | Message ID, approved template key/version, recipient intent, registered disclosure policy key/source reference, safe template data, logical dedup ID and correlation. No arbitrary SMTP endpoint or actor/grant override. |
| `IModuleEmailQueue.EnqueueAsync(ModuleEmailMessage message, CancellationToken ct)` | `Task<ModuleWorkReceipt>`; transactional intent, protected token-bearing material and explicit email-unavailable outcome. |

The concrete attachment read result is frozen as `ModuleAttachmentRead(Stream Content, string MediaType, string DownloadName, long? Length)`. No alternate generic Documents route may disclose a private owner-bound attachment.

Owner-bound attachments are excluded from generic list/search/metadata/download/lifecycle views unless their registered owner policy authorizes the current actor. Missing/disabled owner code fails closed. Standalone Documents retain their existing module permissions. No presigned/public URL mechanism is introduced.

Queued email rechecks current source/recipient disclosure on **every** send attempt, including retries; missing/disabled policies suppress safely. Identity security messages use an explicit identity policy without fabricating organization membership. External email is at-least-once, and already transmitted content cannot be recalled. Business intents join the organization transaction; identity intents require an identity-owned durable boundary before replacing current notifiers. Do not assert atomicity across unrelated EF connections.

## 10. Minimal first slice and exact evidence owners

The first Phase 1 implementation slice is deliberately limited:

- Compile E1 descriptors/provenance and an additive context version behind existing enqueue/envelope callers.
- Use host-internal fixture operation `kernel.parity-probe.v1` and event contract `kernel.parity-probe-committed.v1`, with two stable fixture consumer keys. They are test-only registrations and never enter production module manifests or public routes.
- Prove a UserCommand stops after membership revocation, a committed-event effect remains valid after origin-user revocation but cannot disclose to an unauthorized recipient, and missing scope/raw SQL cannot cross organizations.
- Prove two consumers, one retry, one committed effect each; fan-out commit and crash-after-effect recovery; no retrospective replay of old logging-only records.
- No new business module, scheduler package, runtime RLS bypass, support impersonation, invoice policy or public storage feature is needed to establish this slice.

Test owners: `ModuleExecutionContractTests`, `ModuleWorkExecutionTests`/`ModuleWorkExecutionIntegrationTests`, `ModuleEventDeliveryTests`/`ModuleEventDeliveryIntegrationTests`, existing outbox/grant/isolation regression filters listed in the focused plan. These test names are planned; none is claimed to exist or pass in Phase 0.

## 11. File allocation and compatibility

| Owner | Proposed implementation locations | First caller |
| --- | --- | --- |
| Shared module descriptors | `src/Common/Trykatch.Modules.Abstractions/Execution/`, `Eventing/`, `Jobs/` | E1 registry/E2/E3/E4 |
| Host executor interface/store | `src/Common/Trykatch.Application/Execution/`, Infrastructure `Execution/` | E2 worker execution |
| Outbox metadata/read compatibility | Existing `Application/Outbox/IOutboxWriter.cs`, Infrastructure `Persistence/OutboxWriter.cs`, `OutboxDbContext.cs`, migrations | E1/E3 |
| Grant/provision/inspection profiles | Existing Infrastructure `Organizations/RuntimeDatabaseAccessProfiles.cs`, `RuntimeRoleProvisioner.cs`, `PostgresIsolationInspector.cs`, connection contract and host policy catalog | E1, before any new role/table is used |
| Future shared services | Focused `Caching/`, `Realtime/`, `Features/`, `Quotas/`, `Attachments/`, `Mailing/` contract/adapter files | S1–S6, F1, E5 |

Freeze the ABI only for a contract whose first caller is being implemented; this ledger freezes semantic v1 names/shapes, not speculative executable stubs in every project. Every new public type needs caller compatibility and namespace-generation checks before shipping. Existing published APIs remain supported until their callers are deliberately migrated and verified.

## 12. Phase 0 verification record

Verified by source/document inspection: unchanged upstream baseline, current transaction/grant/permission seams, demo-only permission repair, supported module/template options and direct-package license/framework metadata. Documentation reference/consistency checks and the single independent review are recorded in [Phase 0 evidence](evidence/feature-parity-phase-zero.md).

Not established in Phase 0: executable contracts, new runtime grants/migrations, package resolution/vulnerability audit, scheduler/backplane/cache behavior, runtime isolation of new code, performance or full feature parity. Those remain acceptance rows with planned/qualification status rather than implemented/verified.

## 13. E1 implementation mapping

E1 compiles neutral registration/event/job shapes and host-only execution request/outcome interfaces. It adds a compatible host-only UserCommand enqueue overload and nullable v1 outbox context, not an executor or fan-out adapter. The legacy enqueue path remains explicitly logging-only. Context-version/hash rejection is implemented; active-catalog compatibility before business effects remains E2/E3. See [E1 evidence](evidence/parity-execution-e1.md) for schema/role allocations, manifest tokens, observed checks and the pre-existing dependency-audit blocker.

## 14. E2 implementation mapping

`IOrganizationWorkExecutor` now resolves only explicit module handler/policy adapters in a fresh scope. Its coordinator claim and acknowledgement use the restricted work credential; its effect, completion marker, audit and outgoing intent share the existing actor-scoped organization transaction. The system context has a finite permission set and rejects membership-only use cases. Committed sources retain origin attribution without reauthorizing the origin user; the current resource policy still gates recipients.

`app.module_work_completions` is a separate host-owned organization SELECT/INSERT relation, keyed by organization/work. Immutable envelope/actor/hash binding and marker recovery precede mutable registry/grant/current-membership gates. Exhausted claims can only inspect completion, never run a sixth handler. Acknowledged pauses do not consume the failure budget. Dedicated placement and session/delegation execution fail closed in this version.

Existing v1 user outbox contexts remain supported. Validated system frames emit additive v2 contexts with null membership and optional original actor; legacy payload scope is never guessed. This does not activate E3 delivery or E4 scheduling. See [E2 evidence](evidence/parity-execution-e2.md) for schema, migration, recovery and validation detail.
