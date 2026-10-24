# Feature parity contract ledger

Status: Phase 0 semantic baseline, with E1 contracts, E2 validated execution, E3 durable module event delivery and E4 scheduling and controls implemented and reviewed. Local E4 qualification is recorded; see [E1 evidence](evidence/parity-execution-e1.md), [E2 evidence](evidence/parity-execution-e2.md), [E3 evidence](evidence/parity-execution-e3.md) and [E4 evidence](evidence/parity-execution-e4.md) for scope and verification status. E5 durable email merged in PR #132 after exact-head CI and dependency review passed; [E5 evidence](evidence/parity-execution-e5.md) records its process-restart/browser and final PR checks. The separate Phase 1 installed-event/operation exit merged in PR #133 after exact-head CI, integrated review and packaged-product checks; [consolidated evidence](evidence/parity-execution.md) records the tested revision and merge. Later parity/release work remains open.

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
| Runtime connections and access profiles are distinct and closed; coordinator delivery access is explicitly provisioned. | `src/Common/Trykatch.Infrastructure/Persistence/RuntimeDatabaseConnectionContract.cs`, `Organizations/RuntimeDatabaseAccessProfiles.cs`, `Organizations/RuntimeRoleProvisioner.cs` | E1 extends provisioning and inspection together; E3 uses those exact grants. No existing grant implies access to a new host-only relation; the work coordinator has no business-data access. |
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
5. Resolve supported placement through the host before any new handler effect. Application data shares the same transaction; it may already be enlisted solely for the host completion/inbox recovery described below. Shared placement is the first supported mode. Expose the validated user/system principal explicitly; never fabricate `MembershipId`.
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

The installed registry explicitly binds every consumer descriptor to one module-owned consumer. For an active compatible consumer, `ModuleEventFanout` validates the source event/schema/reader and snapshots registered consumers in the same outbox-role transaction that marks the durable source processed. `ModuleEventFanout` rechecks the durable source and its context/hash/type; database failures roll back the source acknowledgement and snapshots together. This branch does not invoke handlers or external transports. Legacy and no-consumer messages continue through the configured `IOutboxTransport`, including logging and custom transports. Previously processed rows are not backfilled.

Each consumer runs through the bounded organization executor. Its business effect, organization-owned inbox row, required audit and outgoing delivery intent commit in one actor-scoped transaction. One successful consumer is not executed again when another fails. Crash after business commit but before coordinator acknowledgement is recovered from the inbox before mutable registry/grant/policy checks. Follow-up email/realtime/webhook intents are persisted with the effect; network dispatch happens afterward. The delivery worker uses the dedicated `trykatch-work` coordinator connection, bounded leases/attempts and recovery-only checks after business attempts are exhausted.

Disabled consumers are paused with retained records. On re-enable, compatible work can resume. Installing a new consumer does not backfill historical events; previously processed logging-only messages stay processed. A deployment with no enabled new consumers retains the current safe logging behavior rather than silently invoking newly discovered handlers.

Legacy compatibility is explicit: retain `OutboxContractName.For` mappings for Projects/Documents and preserve existing payloads/IDs/replay generations. Nullable new context metadata does not license inventing an organization or actor for an old payload. A legacy message without safely provable scope may finish under its original logging-only adapter or become a visible compatibility outcome; it cannot produce new business side effects. Explicit export DTOs for Webhooks are a different allowlist from internal consumers and from assistant tools.

### Installed Projects creation contract

Projects 1.1 declares `projects.create`/1 as a real `UserCommand`, with `projects.manage`, the `projects` resource policy and the existing `CreateProjectCommand` validation. Its internal module adapter calls the existing creation use case; declaration alone does not make an operation executable or register a publicly submitted job.

Successful creation retains the unchanged legacy `ProjectChanged` record and adds `ProjectCreated(OrganizationId, ProjectId, ActorId)` as `projects.created`/1. The new event carries opaque identifiers, not project name/description. Business state, audit and both outbox rows share the existing organization transaction. Legacy replay keeps its original wire identity and contextless semantics; no old row is promoted into the new consumer protocol. A no-consumer deployment still uses the configured outbox transport.

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
| `ModuleRealtimeNotification` | `ModuleId`, `EventKey`, positive `SchemaVersion`, nonempty `EventId`, and `ModuleRealtimeAudience`. Invalidation references only; no arbitrary body/JSON, group name, organization or cursor supplied by a module. |
| `IModuleRealtimePublisher.PublishAsync(ModuleRealtimeNotification notification, CancellationToken ct)` | `Task`; append durable history in the existing E3 committed-event consumer transaction. Network delivery occurs only after commit and repeats current recipient authority. |

Select HybridCache over a host interface and optional Valkey-backed L2. Read-through cache failure falls back to authoritative reads. Exclude sessions, security stamps, permissions, keys, invitation/reset/MFA tokens, mutable aggregates and sensitive disclosure decisions from cache. HybridCache L1 invalidation/stampede control is per instance; durable invalidation plus authoritative rechecks are mandatory where access changes.

Select same-origin cookie-authenticated SignalR with Origin/antiforgery-aware negotiation and validated audience policies. Database history/inbox is authoritative; a Valkey backplane is lossy and not an outbox. Distributed mode requires its configured backplane; nonlocal messages can be lost during its outage and recover through HTTP reconciliation. The owner relay behavior is refined below. A broken backplane never fabricates success or bypasses audience checks. No standalone SSE endpoint is required for the first parity slice.


### S2 implementation refinement

`ModuleRealtimeAudience` contains a versioned `ModuleRealtimePolicyRef(OwnerModuleId, PolicyKey, PolicyVersion)`, kind `Organization`, `User` or `Resource`, and the matching user/resource selector. Organization has no user selector; User requires a nonempty `UserId`; Resource requires a nonempty `ModuleRealtimeResourceRef(ResourceKey, ResourceId)` and no user selector. Organization/User may include a registered resource hint, which still requires current adapter authorization. All identifiers and selectors are bounded and validated; resource/user IDs are omitted from generic descriptions. The host injects `ModuleRealtimeAudienceContext(OrganizationId, ActorId)` into `IModuleRealtimeAudiencePolicy.AuthorizeAsync(context, audience, ct)`, returning `Task<bool>`.

Installed `RealtimeAudiencePolicies` declare finite unique audience kinds, owner resource keys, required registered recipient permissions, exact event/version pairs and exact `ModuleRealtimeProducerRef(ModuleId, OperationKey, OperationVersion)` consent. `ModuleSystemGrantDescriptor.AllowedRealtimePolicies` supplies reciprocal producer authority. Only installed committed-event operations may publish in this slice. Every declared policy has exactly one explicit scoped `ModuleRealtimeAudiencePolicyBinding`; there is no adapter scanning. Empty recipient permissions are allowed only for explicit user/resource policies evaluated by their current adapter; organization broadcast requires recipient permissions.

A host-owned per-organization counter serializes committed history positions. History is append-only to runtime roles; no retention deletion is introduced. A bounded read advances its opaque continuation after the last scanned row even when no row is disclosed. Continuations bind current organization, actor, durable Identity session and workspace generation; tampered/foreign/obsolete cursors fail closed with explicit reset/refetch. Initial state pairs a head cursor with full domain-query refetch; a null history cursor starts at zero.

Identity's persistent session foundation records hashed issued session identity, current security-stamp epoch, expiry, revocation and workspace generation. HTTP and each realtime delivery validate fresh authority. Logout, current stamp/membership/permission changes and workspace transitions invalidate live access; A→B→A cannot revive an earlier socket. Session administration and password policy remain A1 work. The final current check precedes an awaited bounded send; already in-flight bytes are not retractable, and commit-to-network receipt serialization is not promised.

The implemented session registry stores only a SHA-256 session identity, actor, stamp, timestamps, revocation and a nullable workspace/generation binding. Existing MFA assurance records use that hash as well. The cookie's captured expiry and current durable expiry are checked independently; later HTTP renewal cannot extend an earlier connection's proof. Renewal updates only an existing, unexpired, unrevoked row whose current database user stamp still matches. Fresh sign-in replaces this browser's previous row without revoking other devices. Every workspace cookie write/clear first rotates the current durable generation; same-request invitation activation uses the newly issued trusted session feature. Apply the forward Identity migration before deploying these hooks; existing cookies without a corresponding row require sign-in again. This foundation leaves session administration and retention policy for A1/D4.

Each API relays only its local connections and rechecks current recipient policy per send. The optional standard Redis backplane remains configured and independently qualified in Distributed mode; pinned SignalR local connection sends bypass Redis, so this reference relay does not depend on Redis fanout. This compatibility cost does not make Redis an authorization source or durable queue. Reconnect and periodic browser repair read authoritative HTTP history; pushes only wake that drain. Installed-module removal follows the existing immutable registry deployment boundary: drain/restart every old host before declaring removal complete. No fleet-wide hot module toggle is implied.

## 7.5. HTTP mutation idempotency — S3

| Contract | Implemented shape and authority |
| --- | --- |
| `ModuleMutationPolicy` | Replay lifetime: 1 minute–24 hours (default 24 hours); request bound: 1–65,536 bytes (default 65,536); response bound: 1–1,048,576 bytes (default 262,144), also clamped by outer staging; optional key by default, explicit `KeyRequired` supported. |
| `MutationRequest` | Installed module ID, stable operation ID, SHA-256 key/intent hashes, policy and required current organization permissions. Organization and actor are host-derived. |
| `MutationAuthority` / `MutationResourceReference` | Fresh organization/actor/membership and immutable permission set; optional finite resource key/nonempty opaque ID bound by the server after success. |
| `IModuleMutationIdempotencyStore.ReserveAsync(request, authorizeRequest, ct)` | One attempt per scope in the same live ReadCommitted application/control-plane transaction. Returns `Reserved`, `Completed`, `Conflict`, `InProgress` or `Denied`; only successful states carry fresh authority, only Reserved an opaque token and only Completed a receipt. |
| `CompleteAsync(reservationId, receipt, ct)` | Completes the correct scoped token once in that exact transaction; no use-case invocation, SaveChanges or commit. Persists bounded 2xx status/content type/root-relative Location/exact bytes/resource and expiry. |
| `WithMutationIdempotency(policy, authorizeRequest, authorizeReplay)` | HTTP-only explicit enrollment for an installed, named, secured organization mutation. Separate side-effect-free module gates run before lookup and before receipt disclosure. The prereplay gate receives current authority and the optional stored resource reference. |

The identity is `(organization, actor, module, operation, SHA256(key))`. Keys contain 1–128 ASCII characters, initially alphanumeric and then alphanumeric plus `._:-`; empty, duplicate or invalid keys reject with 400. Required missing keys reject with 400. Bounded preparation follows authorization/antiforgery middleware and precedes body binding. Supported nonempty input is strict UTF-8 `application/json` or `application/*+json`; malformed/duplicate-name/depth over 64 JSON rejects with 400, oversized body returns 413, unsupported media/content encoding returns 415. Canonical protocol 1 includes uppercase method, escaped PathBase/Path, exact raw query, normalized JSON media and canonical body/explicit absence. It reuses the existing exact canonical-number algorithm, sorts object names ordinally and preserves arrays. Cookies/CSRF are excluded from the hash but independently revalidated.

The store attempts an immediate transaction advisory lock over the identity, then re-resolves current membership/permissions and invokes the module request gate before exposing any receipt/conflict/busy outcome. Busy returns 409 `idempotency_in_progress` with `Retry-After: 1`; changed unexpired intent returns 409 `idempotency_conflict`. A completed receipt additionally passes the module prereplay gate before its bytes or Location are returned, without invoking the mutation. Platform or anonymous enrollment is forbidden; a receipt is never authorization.

For newly reserved execution, capture runs after result serialization/BodyWriter flush and before the outer transaction commits. Non-2xx, capture/size/serialization/cancellation/commit failure leaves no successful receipt or committed mutation effect. Existing unenrolled endpoints preserve their previous behavior. Application enlistment remains alive through outer response finalization. Stored 2xx bytes/status/content type/Location alone replay; general headers and old cookies do not. Content type is bounded to 256 characters; Location is a single root-relative path bounded to 2,048 characters. Files, streams, unbuffered/SSE and nonempty bodies on 204/205 responses are rejected; module authors must exclude expiring/signed-URL operations because arbitrary JSON is not content-scanned.

Application response-start callbacks are staged early in the API composition and awaited once in reverse registration order before new receipt capture/finalization. A final non2xx status or callback failure cannot commit the new mutation. Replay permits fresh cookies/general headers while restoring the exact stored response fields and checking body equality. Metadata remains frozen through native publication; the independently observed postcommit network boundary remains after database commit.

Keyed HTTP preparation also rejects case-insensitive property-name collisions at every object depth, including escaped names. Web JSON binding can otherwise make object order change the bound value while the canonical fingerprint stays the same. Shared canonical/outbox hashing remains unchanged; this is an HTTP admission restriction before binding and reservation.

The completed-only host table uses FORCE RLS matching both organization and actor, with SELECT/INSERT/UPDATE only for the organization runtime profile and no runtime DELETE. Runtime profiles never acquire migrator ownership. Apply application migration `20261024113623_AddMutationReceipts` before deployment. The original key/request JSON are not separately persisted, but response bytes remain private. Effective expiry is the earlier of stored expiry and original completion plus current policy lifetime; reducing a policy cannot extend old receipts. Expired replacement commits only with the next successful mutation; after expiry the same key may execute again. No automatic purge or D3 business-retention policy is introduced.

Projects opts in its five mutations and binds the created/route ID. Its gates require current `projects.manage` and org-bound resource existence, including archived/deletion-requested rows, without adding `projects.read`. Organization creation keeps its existing initiating-actor/admin/placement/slug recovery identity and is not generically enrolled. See [ADR 0020](../adr/0020-transaction-aware-mutation-idempotency.md), operations in [English](../operations/idempotency.md) / [French](../operations/idempotency.fr.md), and [qualification evidence](evidence/parity-platform-services.md). These local results do not establish final release readiness.

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

E5's concrete email references are `ModuleEmailTemplateRef(OwnerModuleId, TemplateKey, TemplateVersion)`, `ModuleEmailDisclosurePolicyRef(OwnerModuleId, PolicyKey, PolicyVersion)`, `ModuleEmailProducerRef(ModuleId, OperationKey, OperationVersion)` and `ModuleEmailSourceRef(ResourceKey, ResourceId)`. `ModuleEmailRecipientIntent(RecipientKey)` is a bounded policy-local identifier, resolved by the source owner; it does not choose an SMTP address. `ModuleEmailMessage` preserves separate `MessageId` and `LogicalDedupId`, the template/policy/source/recipient references, `JsonElement TemplateData` and optional `Correlation`.

`ModuleDescriptor.EmailTemplates` and `.EmailDisclosurePolicies` are explicit versioned contributions. `ModuleSystemGrantDescriptor.AllowedEmailDisclosurePolicies` is an additional finite set of policy-version references; it does not change `AllowedResourceKeys` or database grants. A source policy separately consents to exact producer-operation/template-version pairs. Producer modules declare dependencies on source owners; reciprocal source allowlists are references rather than reverse dependency edges and may remain dormant when a producer or template module is absent. Empty policy user-permission metadata makes the policy system-only. User submissions require current membership, every declared permission and current source-policy approval.

The disclosure adapter returns a closed allowed/denied/unavailable result with a current canonical recipient and canonical render data. A compiled template renderer cannot select recipients. Caller template data is input to validation, never an arbitrary body merged into canonical source output. Template identity uses `owner/key` within the host's 160-character purpose/storage bound. Previews use compiled synthetic data and never a stored delivery payload. Admission, current preparation, runtime composition, explicit operator surfaces and generated outputs have focused checks. Automatic production restart/two-replica browser qualification and integrated review are complete; see the E5 evidence for limits and remaining phase/release gates.

Host-owned recovery and invitation requests remain outside business-module metadata. Recovery uses the Identity-owned store. Invitations use `OrganizationInvitationEmailRequest(InvitationId, Token)` and the core `IOrganizationInvitationEmailSubmission` seam, deriving scope/actor from the host and appending to the invitation/audit organization transaction. Its receipt still uses `ModuleWorkReceipt`; no module identity is fabricated. Public invitation results distinguish committed `EmailQueued` intent from acknowledged `EmailDelivered` transmission. The original token and recipient remain protected, and every attempt validates the current usable invitation/role/organization grant. See the [queued-email specification](queued-email.md) for concrete source, origin and unavailability rules.

Operator inspection requires current explicit `platform.email.read`, with no built-in role grant or Administrator read bypass. Administrators retain catalog-wide grant authority so they can create/assign a custom email-reader role. `EmailDeliveryView` exposes only opaque delivery/boundary/organization identity, registered template/version, state, attempt count, timestamps and closed failure/status metadata. Unknown template/error text is masked; the SQL projection excludes protected intent and user/actor/lease provenance. Bounded listing selects one owned queue and never decrypts. `EmailTemplatePreview` renders only compiled synthetic fixtures, with no delivery ID or caller-supplied content. There is no operator replay, purge or arbitrary-send API. Trace correlation uses an opaque delivery ID; metric labels are limited to boundary/outcome. See [operations](../operations/queued-email.md) ([Français](../operations/queued-email.fr.md)).

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

E1 compiles neutral registration/event/job shapes and host-only execution request/outcome interfaces. It adds a compatible host-only UserCommand enqueue overload and nullable v1 outbox context. The legacy enqueue path remains explicitly logging-only. Context-version/hash rejection is implemented; E3 adds active-catalog compatibility before new event effects. See [E1 evidence](evidence/parity-execution-e1.md) for schema/role allocations, manifest tokens, observed checks and the pre-existing dependency-audit blocker.

## 14. E2 implementation mapping

`IOrganizationWorkExecutor` now resolves only explicit module handler/policy adapters in a fresh scope. Its coordinator claim and acknowledgement use the restricted work credential; its effect, completion marker, audit and outgoing intent share the existing actor-scoped organization transaction. The system context has a finite permission set and rejects membership-only use cases. Committed sources retain origin attribution without reauthorizing the origin user; the current resource policy still gates recipients.

`app.module_work_completions` is a separate host-owned organization SELECT/INSERT relation, keyed by organization/work. Immutable envelope/actor/hash binding and marker recovery precede mutable registry/grant/current-membership gates. Exhausted claims can only inspect completion, never run a sixth handler. Acknowledged pauses do not consume the failure budget. Dedicated placement and session/delegation execution fail closed in this version.

Existing v1 user outbox contexts remain supported. Validated system frames emit additive v2 contexts with null membership and optional original actor; legacy payload scope is never guessed. E2 supplies the bounded organization transaction used by E3 consumer effects; it does not activate E4 scheduling. See [E2 evidence](evidence/parity-execution-e2.md) for schema, migration, recovery and validation detail.

## 15. E3 implementation mapping

Installed event consumers are explicitly bound to stable descriptors at module registration. Registry startup checks the installed binding set, committed-event operation and finite system grant; delivery validates the registered event reader. Before any new effect, source event/schema and persisted provenance are checked against the current catalog. A mismatch becomes a sanitized compatibility outcome.

For active compatible consumers, immutable delivery snapshots and source `ProcessedAt` commit together through the existing outbox transaction and role. No handler or network call runs during fan-out. The existing logging/custom transport remains the fallback for legacy traffic and messages with no active consumer. No consumer is inferred for already processed history.

The delivery coordinator uses the `trykatch-work` role for bounded claims/acknowledgements; the effect/inbox/audit/outgoing-intent commit remains in the organization transaction. The operator GET endpoint requires current `OutboxRead` access, returns bounded redacted delivery metadata, and does not expose event payload or authority context. Migration rollout applies additive event-delivery metadata and explicit role grants before the worker uses it. E4 implementation and local qualification are recorded below; E5 durable email is implemented and locally qualified with its remaining remote/phase gates recorded in the E5 evidence. See [E3 evidence](evidence/parity-execution-e3.md); targeted validation and integrated review are complete, with the standard dependency-audit blocker retained.

## 16. E4 implementation mapping

The current source implements user enqueue/schedule through `IModuleJobScheduler`, an opt-in Quartz PostgreSQL adapter, explicit recurring-target bindings, durable cursor/occurrence reconciliation, bounded work forwarding, and platform work inspection/control. Submission requires the current actor and active organization transaction; current membership, permissions and resource policy are rechecked after the request advisory lock and before any idempotent receipt lookup. Intent identity covers canonical payload, mode and normalized UTC due time. The host assigns WorkId, and API acceptance is only published after the outer transaction commits.

Quartz is configured only when `Jobs:Enabled` is true; this stays off by default, OpenAPI generation omits hosted scheduler setup, runtime schema creation is disabled, and there is no in-memory fallback. Only fixed bridges are used: the work bridge carries WorkId and recurring control carries an explicit schedule/target reference. Application migration `20261023221455_AddDurableModuleJobs` creates the 12 base `qrtz_` tables and indexes, and applies FORCE RLS policies scoped to the fixed `trykatch_work_coordinator` role. That coordinator also accesses `module_work_items`, `module_schedule_cursors`, and `module_work_controls`, with no business-data grants. Cursor changes and occurrence work are transactional, with uniqueness by organization/schedule/target/nominal UTC occurrence independent of job version.

Recurring targets are explicit host registrations with inclusive UTC `StartAtUtc`. Catch-up requires both an occurrence bound (1–100) and age bound (positive, no more than 31 days); reconciliation fails visibly if its scan would exceed 100,000 occurrences, without advancing the cursor. Missing current registrations pause retained work/cursors for repair rather than deleting history. E4 operator access uses `platform.jobs.read` and `platform.jobs.manage`; neither is automatically granted to non-admin operators. Controls use request ID plus expected recovery generation and append-only control records; a pause does not cancel an in-flight lease, completed work cannot be retried, and before any new business attempt execution checks current authorization after completion-marker/recovery handling. Host shutdown requests cooperative interruption and waits for active Quartz work within its deadline.

Source and package pins are present. The migration adds schedule cursors, work-control receipts, work-row schedule metadata, occurrence uniqueness, and the adapted Quartz 4.6.0 base schema; its SQL source is Apache-2.0 and identified in the migration. The isolated Quartz/Npgsql dependency graph audit is clean and its resolved licenses are permitted. The E4 merge-gate follow-up patches the recorded Pkcs, Xml and MessagePack advisories; normal locked restore and the solution package audit now pass. Migration application, live role/schema inspection, builds, focused PostgreSQL checks, generated client/UI checks and integrated review passed locally; see the evidence record for exact counts and remaining release limitations. Apply the forward migrator and inspect the exact grants on existing volumes before enabling jobs. E5 remains out of scope. See [E4 evidence](evidence/parity-execution-e4.md) and [operator notes](../operations/module-jobs.md).
