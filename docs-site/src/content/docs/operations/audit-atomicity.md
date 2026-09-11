---
title: Mutation and audit atomicity
description: Operate the commit-before-success boundary and eventual audit-intent projection.
---

Organization business modules commit state, audit entries, and outbox messages in one organization-role transaction. Organization access administration crosses a different schema boundary: it appends an immutable audit intent beside the role, membership, or invitation change, then the constrained worker projects that intent to the organization audit feed. A successful HTTP response is unavailable until the business transaction and intent commit.

Only authenticated scoped `POST`, `PUT`, `PATCH`, and `DELETE` responses are staged, with a 1 MiB default limit. Reads and streaming endpoints are not buffered. Cancellation, oversized output, or a commit exception discards staged success. The HTTP pipeline is never retried. Ordinary role, project, and document creates are not idempotent, so reconcile an ambiguous outcome manually before retrying.

The intent carries a stable event ID, actor, organization, operation, bounded subject metadata, occurrence time, and only allowlisted bounded string details. User deletion reasons are excluded from details; subject display names such as role names remain bounded audit metadata. Database constraints and forced RLS reject arbitrary detail keys and credential fields. The organization role has insert-only intent access. The worker can read intents and insert/select audit entries, cannot mutate intents, restores transaction-local actor/organization settings for every projection, and uses the event ID for idempotent `ON CONFLICT DO NOTHING` delivery.

Audit visibility is eventual, normally within the five-second poll interval. Configure `AuditProjection:BatchSize`, `PollInterval`, `BacklogWarningCount`, and `BacklogWarningAge`. Readiness degrades on a polling failure or sustained threshold breach; monitor `trykatch.audit.projections`, `trykatch.audit.projection.lag`, and the `trykatch.audit.projection.backlog.count` / `trykatch.audit.projection.backlog.age` gauges. Apply both migrations before the emitting API and rerun runtime-role provisioning. Keep intents during rollback/recovery; retention and capacity qualification belong to the release recovery gate.
