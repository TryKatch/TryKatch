+# ADR 0006: Structured audit activity

- Status: Accepted
- Date: 2026-09-07

## Context

Raw action codes, subject identifiers, and actor identifiers are durable storage values but are not a useful operator experience. Formatting them independently in each client would duplicate business vocabulary and make exports or notifications disagree with the web application.

## Decision

Audit activity is a deep backend module with one write interface and one query interface.

Writers provide a stable action code, a typed target with an immutable display-name snapshot, and optional display-safe structured details. Secrets, credentials, invitation tokens, and personal data not required for accountability must never be placed in details.

The application resolves actor identities through the identity adapter and maps stable action codes through a centralized definition catalog. The read contract returns a human title, sentence, category, severity, actor, target, details, and timestamp. Unknown future event codes degrade to readable fallback metadata instead of breaking the activity feed.

The query interface provides organization isolation plus action, target type, actor, time range, free-text, sorting, and pagination. PostgreSQL RLS remains the final organization-isolation control.

## Consequences

- Web, export, CLI, and notification adapters can reuse the same event vocabulary.
- Renamed or deleted resources remain understandable because the event stores the display name at the time of the action.
- Adding an event requires a stable code and preferably one catalog definition; clients do not need conditional formatting.
- Details are intentionally snapshots, not a replacement for domain state or secret storage.

