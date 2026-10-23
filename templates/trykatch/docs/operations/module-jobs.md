# Module jobs operations

E4 module jobs are opt-in. Keep them disabled until the forward migrator has installed the E4 relations and Quartz base schema, the runtime-role provisioner has applied the exact work-coordinator grants, and live schema/role inspection has passed. Application migration `20261023221455_AddDurableModuleJobs` creates schedule cursors, work-control receipts, occurrence metadata, and the 12 Quartz vendor base tables and indexes with FORCE RLS policies for the fixed `trykatch_work_coordinator` role. Existing database volumes must apply that migration and pass live inspection before enabling this worker.

## Enablement

Set `Jobs:Enabled` to `true` only after the database rollout is complete. The API uses the existing `trykatch_work_coordinator` connection. Quartz 4.6.0 uses the PostgreSQL ADO clustered store under `infrastructure.qrtz_`, with JSON serialization, string job data and schema validation. Runtime DDL is disabled. If the connection or schema is invalid, readiness fails; no in-memory scheduler runs as a fallback. OpenAPI generation does not start the hosted worker.

For an existing deployment, use the deployment's one-shot migrator with the migration-owner connection, then run its runtime-role provisioning and inspection path before deploying/enabling the API. Never provide the migrator connection to the API. Verify the exact 12 `qrtz_` base tables plus `module_work_items`, `module_schedule_cursors` and `module_work_controls`; check that the work coordinator has no business-data grants and that control records are insert-only. The Quartz DDL is adapted from Quartz 4.6.0's PostgreSQL schema under Apache-2.0; the migration records its upstream source URL and SHA-256. Local fresh/existing-database migration and exact role/schema inspection passed; see the [E4 evidence](../specs/evidence/parity-execution-e4.md). Deployment rollout still precedes enablement.

## Register a recurring target

Modules declare a stable recurring schedule and a `ScheduledSystem` job/grant in their explicit catalog registration. The host binds a concrete organization target; it does not discover modules by scanning assemblies or accept an arbitrary public cron expression. For a CatchUp schedule, set both explicit bounds:

```csharp
using System.Text.Json;
using Trykatch.Infrastructure.Jobs;

using JsonDocument payload = JsonDocument.Parse("{\"kind\":\"daily-summary\"}");
services.AddModuleRecurringTarget(
    scheduleKey: "reports.daily-summary",
    targetKey: "organization-42", // stable host-owned binding key
    organizationId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
    payload: payload.RootElement,
    startAtUtc: new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero), // inclusive StartAtUTC
    maxCatchUpOccurrences: 24,
    maxCatchUpAge: TimeSpan.FromDays(7));
```

`StartAtUTC` is inclusive: an occurrence exactly at the configured instant is eligible, and reconciliation never creates history before it. Supply both catch-up bounds together; occurrence count must be 1–100 and age must be positive and at most 31 days. Coalesce schedules use the latest nominal instant and do not accept catch-up bounds. The reconciler caps cron scanning at 100,000 occurrences per transaction and surfaces an error without advancing the cursor when that bound is exceeded. Cursor advancement and occurrence insertion commit atomically in one coordinator transaction. Use a less frequent schedule or repair the configuration; do not delete cursor/occurrence history to silence the error.

Recurring target keys are unique per schedule. A changed start time or incompatible persisted configuration fails visibly; deploy a reviewed migration/process to alter a persisted schedule. If a target or module registration disappears, the cursor and work history are retained and affected work is paused. Restoring a compatible explicit registration permits repair. Missing-registration work is excluded from forwarding; changed future trigger readiness is reconciled from the authoritative ledger. Already-forwarded occurrences recheck the current target before a new business attempt.

## Submission and recovery behavior

Use `IModuleJobScheduler` only inside the current authenticated organization transaction. Submission validates the installed UserCommand job, locks the trusted organization/actor/job/version/request identity, rechecks current membership, permissions and resource policy, and only then checks for a prior receipt. Idempotency compares canonical payload, enqueue/schedule mode, and normalized UTC due time. The host assigns WorkId; a request ID is not the work identity. Same-scope identical requests return the original receipt. Reusing the request ID with changed intent conflicts. Another actor or organization cannot learn that receipt or WorkId.

The accepted response is held until the outer organization transaction commits. Quartz forwarding reads committed work and is bounded to 20 candidates per poll. At startup and during polling, the reconciler repairs missing Quartz bridge entries from the work ledger; it does not rely on an atomic EF/Quartz transaction. Bridge data contains only WorkId. The callback reloads authoritative registered work. Before a new business attempt, execution rechecks the current actor's authorization after completion-marker/recovery handling; a previously completed operation is recognized without replaying its effect. Recurring callbacks contain only a registered target reference and materialize occurrence work transactionally with the cursor. The Quartz store uses only the fixed `trykatch_work_coordinator` database role.

Restart and lease recovery preserve WorkId. Retries keep logical identity and use a bounded delay; no external queue/API is invoked from inside the organization transaction. A failed coordinator/database operation remains visible as a readiness failure. On shutdown, Quartz requests cooperative interruption and waits for active jobs within the host shutdown deadline. Use the bounded health/backlog/oldest-age metrics to diagnose delay; metric labels do not include organization, job key or payload values.

## Platform operations

`GET /api/v1/platform/module-work` (`ModuleWork_List`) is read-only and requires `platform.jobs.read`. `POST /api/v1/platform/module-work/{workId}/pause`, `/resume` and `/retry` (`ModuleWork_Pause`, `ModuleWork_Resume`, `ModuleWork_Retry`) require `platform.jobs.manage`; the catalog does not grant that permission automatically to non-admin operators. The API lists bounded pages and a closed set of work states, and returns IDs, state, attempt count, pause flag, generation, timestamps and a safe failure code. Payload, execution context, grant/actor details and raw errors are never exposed.

Every mutation requires a fresh `RequestId` and the displayed `ExpectedGeneration`. The server serializes the request identity, rechecks current access after waiting, locks the work row, applies a compare-and-swap generation change, and records a safe append-only control entry in the same transaction. Repeating the same request returns its durable receipt; a reused request ID with different action/scope or stale generation conflicts. HTTP mutations retain cookie antiforgery and the configured rate limit.

Pause prevents future claims; it cannot cancel a leased execution already in flight. Resume clears the operator pause. Retry is available only for terminal or denied work, resets the attempt budget but keeps the original WorkId and history, and cannot replay completed work. A retried user command still goes through current execution-time authorization, so an operator cannot restore a revoked user's authority. Reconcile the source record and audit history before advising an operator on a terminal outcome.

E5 email delivery, arbitrary user-defined schedules and direct Quartz dashboard/API access are not part of this host surface.
