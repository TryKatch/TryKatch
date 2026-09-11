# Mutation and audit atomicity

## Runtime inventory

| Mutation family | Durable store / runtime role | Transaction and audit contract |
| --- | --- | --- |
| Organization roles, memberships, and invitations | `OrganizationControlPlaneDbContext` / `trykatch_org_runtime` | The actor-scoped control-plane transaction contains both the business change and an append-only `audit_intents` row. |
| Projects and Documents | `ApplicationDbContext` / `trykatch_org_runtime` | Business state, immutable `audit_entries`, and outbox messages share one save and the request transaction. |
| Organization creation, rename, activation, placement | `PlatformDbContext` / `trykatch_platform_runtime` | One platform transaction; creation also uses its stable creation intent and slug/idempotency identity. These global control-plane events are not organization audit-feed events. |
| Platform access and local account/profile/security changes | `IdentityDbContext` / `trykatch_identity_runtime` | Identity operations own explicit single-context transactions where multiple writes occur. MFA/security outcomes use the global append-only Identity security audit rather than the organization feed. |
| Federation connection administration | direct Npgsql / `trykatch_identity_runtime` | Each mutation is one SQL statement. Secret material remains protected and never enters organization audit details. A global operator-audit feed is a separate security feature. |
| Invitation acceptance | `OrganizationControlPlaneDbContext` / `trykatch_org_runtime` | An explicit actor-scoped transaction and organization advisory lock commit invitation consumption and membership creation together. |

Organization-scoped HTTP mutations enlist `ApplicationDbContext` on the active control-plane connection and transaction. Both contexts therefore use the same permitted organization role, physical connection, transaction-local `app.organization_id` / `app.actor_id`, and commit outcome. The HTTP response body feature is staged only for authenticated scoped `POST`, `PUT`, `PATCH`, and `DELETE` requests. Reads and streaming responses are not buffered. The default staged-response limit is 1 MiB (`AtomicMutationResponse:MaximumBytes`); exceeding it fails closed and rolls back. The downstream HTTP pipeline is invoked exactly once and is never execution-strategy retried.

Cancellation before commit prevents success publication. A commit exception is treated as an ambiguous failure: staged success bytes are discarded and the client must reconcile before retrying. Organization creation has an explicit idempotency identity and audit projection uses its stable event ID, but ordinary role, project, and document creates are not idempotent. Do not automatically retry an ambiguous non-idempotent create; reconcile it manually first, and do not add blind HTTP retries.

## Audit intent and projection

`platform.audit_intents` is immutable at runtime. The organization role has `INSERT` only, with forced RLS binding organization and actor to transaction-local settings. PostgreSQL constraints require non-empty identifiers, a bounded JSON object, string/null values, and only server-derived `expiresAt`, `permissionCount`, `reasonProvided`, `roleCount`, and `status`. User-entered deletion reasons remain only on protected business records; structured details store counts or the boolean fact that a reason was supplied. Subject display names, including role names, are separate bounded audit metadata. Passwords, credentials, tokens, authorization values, client secrets, raw exceptions, and arbitrary detail fields are rejected.

The outbox worker role can read intents and insert/select audit entries but cannot update or delete either relation. The projector opens a new transaction per event, re-establishes organization and actor settings, and inserts the audit entry using the intent event ID with `ON CONFLICT DO NOTHING`. A failed projection leaves the committed intent pending; replay is eventually exactly once in the audit store.

Projection is intentionally eventually visible (normally within the five-second poll interval). `trykatch.audit.projections` counts bounded outcomes, `trykatch.audit.projection.lag` records observed lag, and the `trykatch.audit.projection.backlog.count` / `trykatch.audit.projection.backlog.age` gauges expose the live backlog. `/health/ready` degrades after a polling failure, 1,000 pending intents, or two minutes of oldest-intent age by default. Configure `AuditProjection:BatchSize`, `PollInterval`, `BacklogWarningCount`, and `BacklogWarningAge`, and alert on sustained degradation. Retention and long-term growth qualification are deferred to the PR 15 recovery/capacity gate; do not delete intents as an operational shortcut.

Upgrade by applying the Platform and Application migrations before deploying API instances that emit intents, then run runtime-role provisioning so the exact insert/read/projection grants are rebuilt. Rolling back the code after intents are emitted delays their projection; retain both intent and audit tables and roll forward.
