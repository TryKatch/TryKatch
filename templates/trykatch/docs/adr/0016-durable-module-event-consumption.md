# ADR 0016: Durable, explicit module event consumption

Status: Accepted and implemented in E3; targeted verification and integrated review are recorded separately. See [E3 evidence](../specs/evidence/parity-execution-e3.md).
Date: 2026-10-23.

## Context

[ADR 0010](0010-transactional-outbox.md) already provides durable at-least-once outbox storage, stable message identity, bounded failure and controlled replay. Its default transport logs messages and returns success. That is a safe no-broker baseline, not delivery to Notifications, Billing or Webhooks.

Fan-out introduces independent consumers. Retrying an entire event after one consumer commits can duplicate effects; acknowledging before per-consumer intent is durable can lose work. CLR names or current application namespaces are not a safe contract versioning strategy for new events.

## Decision

Use explicit event descriptors and stable consumer keys registered by enabled modules. A descriptor binds event key/schema version to a reviewed reader; no serialized CLR type activation or catch-all export of internal events is allowed.

The host fan-out path persists a snapshot of compatible installed consumers and their delivery records in the same transaction as acknowledging the source outbox row. The implementation re-reads and validates the durable source row; a database failure rolls back both acknowledgement and snapshots. A later connection/commit is not atomic fan-out.

Each delivery invokes the bounded [ADR 0015](0015-bounded-background-execution.md) execution boundary. The organization transaction commits the consumer's database effect, a unique `(OrganizationId, MessageId, ConsumerKey)` inbox marker, required audit and any outgoing delivery intents together. A consumer code upgrade retains its key; payload schema version is recorded separately rather than appended to a dedup key to bypass history.

One successful consumer stays successful when a peer fails. After a crash between effect commit and coordination acknowledgement, the next attempt observes its committed inbox marker and acknowledges without reapplying the effect. Claims/leases, attempts, terminal state and replay generation are durable and bounded.

Outgoing email/realtime/webhook intents are persisted **before** the source effect transaction commits; actual network dispatch occurs after commit. Database deduplication does not make SMTP/HTTP exactly once: keep a stable logical delivery ID, receiver dedupe guidance and sanitized attempt history.

Disabled consumers pause and retain pending/history data. Compatible re-enable can resume; a new consumer does not backfill old events automatically. Installing a module never resets processed logging-only records to trigger newly introduced business effects.

Preserve the existing Projects/Documents `OutboxContractName` mappings, payload readers, message IDs and replay semantics. New metadata is additive/versioned. Legacy rows with no provable trusted scope remain on an explicit original logging-only path or a visible compatibility outcome; they cannot be assigned guessed actor/organization values. A no-consumer configuration retains the existing safe baseline.

Webhooks export descriptors and assistant-tool descriptors are separate allowlists. Internal event consumption is not consent to external disclosure or assistant execution. Current resource/recipient policies still apply.

## Alternatives

| Alternative | Tradeoff |
| --- | --- |
| Inline handler invocation inside the source request | Makes independent side effects/request success tightly coupled and cannot recover failed external delivery safely. |
| Mark event done then enqueue consumer work | Has a crash window that permanently loses effects. |
| One aggregate retry flag for all consumers | Reexecutes peers that already committed or requires every business effect to invent its own independent retry store. |
| Require a broker for the first implementation | Adds operational scope without removing inbox/idempotency requirements; a broker adapter can be added later. |

## Consequences and proof

No broker is required for the first slice. Durable fan-out/inbox tables and explicit operational grants are required; see [ADR 0017](0017-operational-data-access-profiles.md). Event registry validation joins the existing doctor/catalog checks without runtime scanning.

Required cases: two consumers with one failure; duplicate publication; competing workers; crashes before fan-out commit/after effect commit; unknown schema/version; consumer disable/re-enable; no historical logging-only backfill; atomic notification follow-up intent. Implementation and evidence scope are documented in [E3 evidence](../specs/evidence/parity-execution-e3.md); the standard dependency-audit blocker remains open.
