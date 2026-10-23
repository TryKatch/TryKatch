# ADR 0017: Explicit operational data ownership and access profiles

Status: Accepted for the parity implementation architecture. No roles, tables or privileges are changed by this ADR.
Date: 2026-10-23.

## Context

Current runtime credentials are distinct organization, platform, identity and outbox profiles. `ModuleDataResourceRules`, `RuntimeDatabaseAccessProfiles`, the migrator and live inspector use a closed ownership policy. Declaring `host-only` does not automatically grant any role access. Adding jobs/inboxes through a broad schema grant would bypass the existing model.

The shared database permits atomic organization effects/inbox/audit/outbox, but a future dedicated database changes that transaction boundary. Editable Billing plans also cannot be mislabeled `global-read-only`: organization reads of reference data do not authorize platform writes to it.

## Decision

Retain existing roles and add one separate operational profile/connection at implementation: `trykatch_work_coordinator` / connection key `trykatch-work`. Provision it outside runtime startup with no ownership, superuser, `BYPASSRLS`, `CREATEROLE` or schema creation privileges. The one-shot migrator owns schema changes. Existing production connection validation/deployment bootstrap/inspector must be extended atomically before this credential is required.

The coordinator can claim/schedule/update exact operational relations, not organization business tables, Identity, role grants or raw platform administration. Business execution enters the host organization transaction under [ADR 0015](0015-bounded-background-execution.md); trusted in-process modules are not a sandbox boundary, and all such execution must preserve the host context/authorization contract.

### Proposed relation profiles

Names are frozen allocations for their owning tasks, not tables already present. Every relation needs host schema declaration, immutable migration, exact grant mapping, indexes and live inspector coverage before use.

| Relation | Ownership / minimum runtime access |
| --- | --- |
| `infrastructure.module_work_items` | Host-only operational: organization runtime INSERT of trusted work intent; coordinator SELECT/INSERT/UPDATE for registered scheduling/claim state. No public module/HTTP table access; no automatic DELETE. Organization-origin insert policy must bind persisted organization/actor to current host transaction. |
| `infrastructure.module_event_deliveries` | Host-only operational: outbox worker SELECT/INSERT for atomic fan-out; coordinator SELECT/UPDATE for claim/ack/retry. No business CRUD or generic grant through module metadata. |
| `infrastructure.module_recurring_schedules` | Host-only operational: coordinator SELECT/INSERT/UPDATE of registered schedule state only. |
| Quartz PostgreSQL adapter relations | Host-only operational: coordinator exact scheduler-required SELECT/INSERT/UPDATE/DELETE and required sequence use on the versioned vendor schema only. Migrator imports/declares the pinned schema; never auto-create at runtime or wildcard-grant all infrastructure tables. List exact relations at E4 from the selected package schema and checksum. |
| `app.module_event_inbox` | Host-owned organization relation, forced RLS and organization-runtime SELECT/INSERT; uniqueness `(OrganizationId, MessageId, ConsumerKey)`. Ordinary business code has no broad inbox-delete permission. Add inspection/profile support for this narrow host relation instead of generic org CRUD. |
| `app.module_request_idempotency` | Host-owned organization relation, forced RLS; current-actor/operation-bound SELECT/INSERT/UPDATE for transactional request state; retention DELETE is separate governed maintenance. |
| `app.organization_entitlements` | Host-owned organization read projection; organization runtime SELECT, approved host publisher INSERT/UPDATE in validated organization scope. No platform direct SELECT of invoices/subscriptions or editable global plan bypass. |
| `app.organization_quota_reservations`, `app.organization_usage` | Host-owned organization relations with forced RLS. Reservation state has bounded transaction transitions; usage ledger is append-only/idempotent. Exact distinct grants follow their operations rather than blanket CRUD. |
| Permission catalog version records | Host-owned organization state and audit, used by S6 in the same transaction as protected Owner reconciliation. Existing user roles keep their current profiles. |

The table is an access contract, not executable SQL. E1/S3/S5/S6 must add narrow host exceptions because the current generic organization resource profile grants CRUD; simply labeling a host relation organization-owned is insufficient. RLS guards organization identity even for raw SQL; actor/operation guards are added where a user-private host relation requires them.

Operational records can contain protected customer payloads; the coordinator is trusted host infrastructure. Platform work APIs query sanitized allowlisted metadata through a dedicated host read seam under `platform.work.read`, not by giving the platform role general direct access to every operational payload. Control actions under `platform.work.manage` validate work/version/action and append audit; they cannot select arbitrary handlers, SQL, credentials or an impersonation target.

### Transaction boundaries

- Source domain/audit/outbox and user work intent: one organization transaction on the shared data connection.
- Source outbox processed flag plus fan-out records: one outbox/coordinator-compatible transaction with exact new grants, no separate commit.
- Consumer effect/inbox/outgoing intent: one validated organization transaction; coordination acknowledgement can follow and is safely deduplicated.
- Quartz forwarding: after Trykatch intent commit, idempotent work-reference registration; no assumed distributed transaction with Quartz.
- Identity email intents: identity-owned durable transaction/store before replacing identity notifiers; do not reuse unrelated org context.
- Dedicated placement: excluded from these shared-database atomicity claims until D6 defines cross-database routing/outbox/inbox and recovery.

### Billing and permission upgrades

Editable plans are platform-owned under explicit platform permissions. Publish an immutable safe organization entitlement/read projection through a host seam; do not extend `global-read-only` to make it writable or reference Billing implementation from kernel code.

S6 uses the current additive `Role.AddMissingPermissions` primitive to reconcile the protected Owner against a validated installed catalog version. It must not automatically broaden existing Admin/Member/Viewer/custom-role grants. Current development-demo repair is not production upgrade support. Disabled/unknown keys remain filtered by the active catalog, and no stale role row authorizes a disabled module.

## Alternatives and consequences

Reusing/broadening the outbox worker for all business access was rejected: its existing role is deliberately narrow. Blanket infrastructure or organization CRUD was rejected: ownership alone must not grant unintended retention or coordination authority. Giving platform operators operational payload/tenant access was rejected: permission catalogs remain separate.

E1 must change connection contracts, bootstrap provisioning, migration grant rebuild and inspector tests together; otherwise startup must fail rather than silently fall back to a shared privileged credential. No fallback from a missing coordinator connection to `trykatchdb` is approved for this new profile in production.

Proof requires live PostgreSQL inspection for all six concerns: declared relation, expected ownership, exact grants, relevant RLS/actor policies, nonprivileged roles and missing-context default denial. Existing outbox recovery/audit isolation continue to pass; no database or runtime proof is claimed by this documentation-only phase.
