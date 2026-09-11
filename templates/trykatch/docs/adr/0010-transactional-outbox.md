+# 0010: PostgreSQL transactional outbox with an explicit transport seam

- Status: Accepted
- Date: 2026-09-08

## Decision

Application use cases enqueue version-stable event type names and JSON payloads in PostgreSQL within the same transaction as domain state. A background processor claims pending rows with `FOR UPDATE SKIP LOCKED` and publishes an `OutboxEnvelope` through one `IOutboxTransport` interface.

Delivery is at least once. The envelope message identifier is the required idempotency key for every transport adapter and downstream consumer. A broker-free logging adapter is the safe version-one default; applications replace that registration when integration events must leave the process.

The hosted worker creates a fresh asynchronous dependency-injection scope for each bounded batch. A scoped processor owns row claiming, external delivery, durable failure/terminal state, replay outcomes, and database commit or rollback. Database execution-strategy retries never surround publication. Recoverable provider faults dispose the failed scope and use bounded exponential jitter; permanent authentication, TLS/configuration, schema, grant, or programming faults stop dispatch and fail readiness until an operator restarts the process.

Ten durable failures exhaust the current replay generation by default. Terminal state remains excluded when configuration changes. Platform administrators request replay asynchronously using an immutable request identifier and expected failed generation; the worker records one immutable outcome and, only for the matching exhausted generation, resets failure state and increments the generation without changing the logical message identifier, type, payload, or occurrence time. No automatic replay exists.

## Consequences

Business transactions do not depend on a message broker. Multiple API replicas can process the table without claiming the same row concurrently. A crash after publish but before commit can redeliver an envelope, so adapters must preserve the message identifier and consumers must deduplicate effects. Payloads are not written to logs by the default adapter.

This is not exactly-once transport. A consumer must atomically record `MessageId` with its business effect. A successful publish followed by rollback or an uncertain commit may redeliver the same identifier. A committed processed timestamp prevents publication after fresh-state recovery. Payloads above one MiB are terminalized by a server-side byte guard before materialization.
