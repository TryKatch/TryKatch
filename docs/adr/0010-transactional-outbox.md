+# 0010: PostgreSQL transactional outbox with an explicit transport seam

- Status: Accepted
- Date: 2026-09-08

## Decision

Application use cases enqueue version-stable event type names and JSON payloads in PostgreSQL within the same transaction as domain state. A background processor claims pending rows with `FOR UPDATE SKIP LOCKED` and publishes an `OutboxEnvelope` through one `IOutboxTransport` interface.

Delivery is at least once. The envelope message identifier is the required idempotency key for every transport adapter and downstream consumer. A broker-free logging adapter is the safe version-one default; applications replace that registration when integration events must leave the process.

## Consequences

Business transactions do not depend on a message broker. Multiple API replicas can process the table without claiming the same row concurrently. A crash after publish but before commit can redeliver an envelope, so adapters must preserve the message identifier and consumers must deduplicate effects. Payloads are not written to logs by the default adapter.

