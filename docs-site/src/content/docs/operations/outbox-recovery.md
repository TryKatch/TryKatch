---
title: Outbox recovery and replay
description: Operate bounded at-least-once dispatch and authorized replay safely.
---

The outbox worker uses a fresh dependency-injection scope and database transaction for every bounded batch. Transient PostgreSQL outages dispose the failed scope and retry with bounded exponential jitter. Authentication, TLS/configuration, schema, grant, and programming faults stop dispatch and make readiness unhealthy until an operator corrects the fault and restarts the service.

Delivery is **at least once**. Publish success followed by rollback or an uncertain commit can redeliver the same logical message ID. Consumers must atomically deduplicate `MessageId` with their business effect. No transport adapter may claim exactly-once delivery.

After the configured durable-failure limit, a message and immutable terminal event commit together. Lowering the limit terminalizes already over-limit rows without publishing. Payloads larger than one MiB are detected server-side and terminalized without materialization. Telemetry contains only bounded outcomes, counts, ages, durations, exception types, and IDs as trace/log metadata—never payloads, raw errors, SQL, parameters, credentials, or actor names.

Operator and Auditor can read bounded terminal metadata. Administrator can submit a replay request with a stable request ID and the observed failed generation. The request returns `202` only after its immutable row commits. Reusing the same request ID and tuple is idempotent; a different tuple is `409`. The worker records exactly one `replayed`, `not_found`, `not_exhausted`, or `stale_generation` outcome. Replay preserves message ID, type, payload, and occurrence time while incrementing the generation.

For deployment: quiesce all workers, verify a backup, apply the migration, provision and inspect grants/RLS, then deploy the new API and workers together. Mixed worker versions are unsupported. Do not downgrade by deleting recovery history.

The transport deadline releases the batch after recording a safe failure. A cancellation-ignoring adapter makes readiness unhealthy and blocks further publication in that process until it completes; shutdown does not wait indefinitely. Other replicas can still redeliver, so consumer deduplication is essential. Replay rechecks the immutable completion marker under per-message transaction serialization. Request-ID idempotency includes the authenticated actor. Polling a replay returns a documented `202` pending body until the `200` completed outcome is available.
