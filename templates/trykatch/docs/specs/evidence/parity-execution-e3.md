# Phase 1 E3 — durable module event delivery

Status: E3 implementation, targeted validation and integrated review are complete. The standard dependency-audit gate remains failing as recorded below. E4 scheduling and E5 queued email remain unimplemented. This evidence describes the bounded event-delivery slice only.

## Explicit registration and source validation

For example, in the owning module's `Register` method (alongside the matching manifest/descriptor operation, finite grant and resource policy):

```csharp
services.AddScoped<OrderCommittedConsumer>();
services.AddScoped<ModuleEventConsumerBinding>(provider => new(
    "inventory", "inventory.reserve-order",
    provider.GetRequiredService<OrderCommittedConsumer>()));
```

`OrderCommittedConsumer` implements `IModuleEventConsumer`. Its committed-event operation can use this explicit binding without a second `IModuleOperationHandler`. User and scheduled operations still require their explicit operation handler. Constructors defer data access until the bounded execution invokes them.

Each installed module explicitly registers a `ModuleEventConsumerBinding` containing its stable consumer key and implementation. Startup validation requires exactly one binding for each installed consumer descriptor and rejects missing, duplicate or unknown bindings. The registry resolves the consumer-owned committed-event operation and finite system grant; it does not discover handlers through assembly scanning or payload type names.

Before fan-out or effects, the registry validates the source event key/schema and registered payload reader against the installed catalog. Source provenance and actor/grant fields are checked against the registered operation. Invalid, malformed, unknown-version or mismatched sources receive a fixed compatibility failure code. Payload bytes are not copied into delivery snapshots or diagnostics.

## Transactional fan-out and per-consumer effects

For a compatible event with active consumers, `OutboxDelivery` awaits the internal `ModuleEventFanout` path. It locks and re-reads the durable source outbox row, compares the caller envelope to its persisted message identity, organization, event type, context and payload hash, and snapshots each consumer from the current registry. The snapshot records immutable provenance, operation/schema versions, payload hash, replay generation and the registered grant's effective audit actor.

Delivery rows and the source `ProcessedAt` acknowledgement are saved by the same outbox-role context and transaction. A database failure propagates and rolls back the source acknowledgement and all new snapshots together. A previously processed source is not snapshotted again; there is no historical backfill. Existing rows are reused only if their immutable snapshot matches, and completed state is never reset. The fan-out branch runs no handler or network call. With no active compatible consumer, legacy messages and no-consumer configurations continue through the configured `IOutboxTransport` (including the logging default and existing custom transports).

Each delivery then runs through the bounded organization executor. The consumer's business effect, unique inbox marker `(OrganizationId, MessageId, ConsumerKey)`, required audit and outgoing intent commit in one actor-scoped organization transaction. The coordinator delivery is acknowledged only after that commit. If acknowledgement is lost, inbox recovery recognizes the committed effect before rechecking mutable module/grant/policy state and avoids applying the effect twice. A peer consumer's retry does not rerun a successful consumer.

## Coordinator limits, recovery and access

The delivery store uses only the dedicated `trykatch-work` coordinator connection. It claims due deliveries with short `FOR UPDATE SKIP LOCKED` transactions and token-checked leases. Processing has a two-minute lease, one-minute cancellation deadline, five business attempts, a five-second minimum retry delay, and a bounded batch of 20. After the business-attempt limit, a recovery-only claim may inspect the inbox and acknowledge a known committed effect, but cannot invoke a sixth business attempt. Pauses restore the attempt budget and retain the record; re-enabled compatible consumers can resume, including when the last consumer and its unused operation/grant were removed. Claims order by readiness time so repeatedly paused old rows do not starve earlier-ready work. Cancellation rolls back effects and releases the token lease through bounded cleanup.

Runtime roles remain separated: the outbox role reads source messages and inserts delivery snapshots; the work coordinator reads/updates delivery state; organization roles read/insert the inbox under forced organization RLS. The coordinator receives no business-table access. Schema and grants are additive through the host migration and runtime-role provisioning path; deployment must apply pending migrations and provision roles before the worker uses the new tables.

Operators can inspect deliveries at `GET /api/v1/platform/outbox/module-deliveries` under `PlatformPermissions.OutboxRead`. The endpoint and directory both enforce current access before reading coordinator state. Page and page size are bounded, state filters use a fixed allowlist, and output contains only delivery/message/organization IDs, consumer key, state, counts, replay generation, timestamps and a closed failure code. Payload, actor/grant context, recipients, credentials and raw stored diagnostics are never returned.

## Compatibility and rollout boundary

Forward Application migration `20261023190855_AddDurableModuleEventDelivery` adds delivery snapshot and inbox metadata without reconstructing events from old outbox history. Historical delivery/inbox rows retain explicit empty/default metadata rather than inferred authority and fail compatibility checks before effects. Existing processed logging-only rows remain processed, and newly installed consumers do not receive previously acknowledged history. Unprocessed compatible events snapshot the consumers installed when fan-out commits. New event side effects require current registered event/schema/operation/grant compatibility. Legacy and no-consumer traffic retains the configured transport behavior. Migration and runtime-role provisioning must complete before enabling the event worker in a deployment.

E3 persists outgoing intents with effects but does not dispatch SMTP, HTTP or realtime traffic. Public work submission, delayed/recurring scheduling and scheduler integration (E4), and durable queued email/template delivery (E5) are not implemented by this phase.

## Verification record

SDK 10.0.301; PostgreSQL 18.6 Alpine 3.23 at the committed image digest. PostgreSQL tests executed against real containers and restricted runtime roles, with zero skips. Diagnostic checks retain NuGet audit with `-p:WarningsNotAsErrors=NU1902%3BNU1903`; dependencies and lock files are unchanged.

| Check | Result |
| --- | --- |
| Unchanged-source baseline | 53 affected E2/outbox/audit PostgreSQL cases passed, zero skips. |
| Focused unit tests | 88 passed, zero skips: event registry/bindings/readers, E2 execution/grants/principals, outbox v1/v2 delivery/recovery, manifest/contracts, runtime connection and audit behavior. |
| PostgreSQL integration | 111 passed, zero skips: all 15 E3 cases plus affected E2 execution, outbox migration/recovery/operator HTTP, audit atomicity, RLS inspection, role provisioning and organization HTTP scope. |
| E3 acceptance | Independent consumer retries with atomic follow-up intent, fan-out transaction rollback/restart, effect rollback, lost acknowledgement including the fifth attempt, duplicate/competing workers, unknown schema and forged organization, disabled last consumer pause/re-enable, no processed-history backfill, current recipient denial, readiness fairness, cancellation rollback/lease release, hosted worker processing/shutdown. |
| Debug solution build / OpenAPI | Passed with existing audit warnings visible; OpenAPI rebuilt and clients regenerated. |
| Module doctor / architecture | Healthy; host plus Projects/Documents/Federation architecture: 12 + 5 + 5 + 5 passed, zero skips. |
| React API surface | Frozen install, generation/check, workspace typecheck and production build passed. API client: 7 tests passed. No UI behavior changed; browser and unrelated UI suites not run. |
| Packed renamed React / backend-only templates | Pack/install/generation passed. Each renamed output passed 28 focused registry/executor/outbox-context unit tests and all 15 real PostgreSQL E3 cases, zero skips. Backend-only generation/build produced OpenAPI without a web workspace. |
| Integrated review | Two findings fixed in the same review: last-consumer removal must pause before operation lookup; readiness ordering must avoid starvation by repeatedly paused old rows. Both reproduced red, fixed and covered by green PostgreSQL regressions. No unresolved actionable findings. |
| Standard locked restore | Failed on existing Pkcs 10.0.0 NU1902/NU1903, Xml 10.0.11 NU1903 and AppHost MessagePack 2.5.302 NU1902 advisories. MessagePack is present in the unchanged committed lock file. The PR remains draft; diagnostic passes do not establish release readiness. |

Commands ran from the application root. Integration selected `ModuleEventDeliveryIntegrationTests`, `ModuleWorkExecutionIntegrationTests`, `OutboxRecoveryIntegrationTests`, `OutboxMigrationTests`, `AuditIntentAtomicityTests`, `PostgresIsolationInspectionTests`, `PostgresRuntimeRoleFixtureTests` and `OrganizationScopeMiddlewareTests`. The unit filter selected the corresponding module-event/work, outbox context/delivery/recovery, execution contract/manifest/principal, runtime connection and audit classes. Checks used `dotnet test --filter`; a Debug `dotnet build Trykatch.slnx` preceded `--no-build` architecture tests and `dotnet run --project tools/Trykatch.ModuleTool --no-build -- module doctor`. Frontend checks used the committed pnpm version from the web workspace.

The forward migration ran against isolated databases and the EF model matched the migration snapshot. Existing exact role/RLS inspection checks stayed green; E3 adds no broader grants. No deployed database, external SMTP/HTTP provider or scheduler was exercised. E4/E5 and the remaining Phase 1 common exit stay open.
