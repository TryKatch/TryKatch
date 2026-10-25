# Closed quota maintenance evidence

The S5 host schedules and executes only three closed maintenance kinds: transaction-only reservation expiry, provisional-upload fencing and installed resource inventory. No business handler, object key, customer payload, actor override or permission set is selected by a queue row. Existing queue schema, forward migration, immutable guards and exact runtime profiles are retained.

## Implemented boundary

Public transaction-only Reserve inserts its matching expiry reference inside the caller's transaction; rollback removes both. Durable upload admission and incomplete inventory already persist their own references. The coordinator store uses only `infrastructure.quota_maintenance_work`, short bounded operations, database time, row exclusion and two-minute token leases. Finish/release require current organization/id/token and an unexpired lease. Attempts saturate rather than imposing a terminal cutoff; retry backoff is bounded at 5 seconds to 5 minutes, progress at 5 seconds and pause at 60 seconds. Completed references cannot reopen.

The processor executes each claimed item through a new scope with a 60 second deadline and acknowledges afterward. The host executor revalidates the actual durable reservation/upload/resource and immutable effect kind under the shared resource lock, through the existing fixed empty-permission authority. Transaction-only expiry never releases an Upload-kind hold, including when its upload record is absent. Upload handling closes the validating transaction before invoking the existing fencing core: committed uploads stay protected, Fencing commits before storage mutation, a permanent sentinel is independently confirmed, and only then does a fresh transaction release the hold. Previously admitted uploads remain recoverable with Documents absent and original membership revoked. Inventory invokes only its exact installed source and retains atomic accounting/checkpoints.

The registered background worker processes at most 20 items per batch and reports aggregate readiness/metrics. Coordination failure is unhealthy; retained retry/paused or overdue work degrades health. The backlog age comes from due time, not creation time, so future work does not falsely appear overdue. OpenAPI generation omits the worker. There is no new schema/profile or configurable business authority.

## Qualified local evidence

Successful PostgreSQL runs use actual restricted runtime roles and have zero skips. The final 100-case selection plus the distinct future-age and lost-acknowledgement cases qualify 102 unique cases (24 new maintenance/scope cases and 78 directly affected existing cases). Overlapping runs are reused rather than counted twice.

| Evidence | Scope |
| --- | --- |
| `quota-maintenance-final-pg` | 100 selected PostgreSQL cases, all 92 selected methods present: new store/executor/worker cases plus directly affected quota, entitlement publication, inventory, fencing, technical-scope and exact queue-profile checks. |
| `quota-maintenance-health-green` | 6 health checks: unavailable before observation, safe aggregate fields, retry/paused/old backlog, successful recovery after coordination failure. |
| `quota-maintenance-age-green` | 3 affected PostgreSQL cases after the due-age correction: future due work, aggregate backlog and registered worker loop. Adds one distinct case to the earlier selection. |
| `quota-maintenance-recovery-boundaries` | Lost acknowledgement after actual fencing/release commit recovers under a new lease without changing the sentinel revision; storage unavailable beyond five attempts retains the hold, then a fresh processor completes after storage recovery. The storage case strengthens an existing selected method. |

Store coverage includes concurrent independent coordinator claimers, future/active/completed filtering, expired lease reclaim and stale/expired acknowledgement/release rejection, wrong-organization acknowledgement, no terminal cutoff, Int32 saturation, stable completion and producer rollback. Execution coverage includes abandoned uploads before/after PUT, permanent sentinels and late writers, committed-state protection, inactive and unsupported placement pause/resume, absent Documents/original membership, inventory accounting, and a real registered background worker start/stop. The transaction-rebind acceptance check already passes under the existing authority guard; no new authority workaround was introduced.

## Failures investigated

The health scaffold first fails on missing types, then has five semantic failures and one passing initial-unavailable case. All six pass after implementation. The maintenance API first fails compilation on missing store types. After an additional nullable assertion fixture correction, the unavailable scaffold has 15 failing cases and three passing no-op denials. Store/executor/producer implementation passes all 18 initial PostgreSQL cases.

The first hosted-worker test enumerated unrelated Quartz hosted services without an application lifetime. The isolated fixture now retains only the actual quota worker registration; its next run fails on the missing registration, then passes after the worker is registered and implemented. Absent-module and placement cases pass independently. The additional future-due case initially exposes creation-time ageing; using due time corrects it and the affected three-case run passes. No schema, role or business requirement was weakened.

## Remaining acceptance

These are local database/filesystem and hosted-worker proofs. A new processor/store and durable lease recovery are not claimed as an operating-system process kill or storage-provider restart. Task6 still requires its remaining actual process-loss/restart and storage fault boundaries, alongside the composed RustFS/two-replica/absent-source journeys required in Task8. Safe initializing usage inspection, distributed request throttling, usage UI, freshly packed/renamed products, the single whole-S5 review and final-head CI/merge remain open. S6, phases3–11 and the final main release remain required. No physical purge, retention policy or quota-aware mixed-old-software deployment is qualified here.
