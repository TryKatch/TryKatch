# ADR 0007: Recoverable record lifecycle

- Status: Accepted
- Date: 2026-09-07

## Decision

Organization-owned operational records use three persisted lifecycle states: Active, Archived, and Deleted. The product presents `Deleted` as **Pending deletion** because physical disposal has not occurred. Archive is a durable, reversible inactive state. A deletion request requires a 10–500 character reason, records the actor and time, and remains restorable by an authorized user. Physical deletion is available only through a separately authorized retention-policy workflow; immutable audit events are excluded from this lifecycle.

Transitions are resource-specific. Projects, memberships, and custom roles must move from Active to Archived before deletion can be requested. Invitations do not support Archive: pending invitations can be revoked, and invitation records can be deleted directly with a reason. System roles cannot be archived or deleted, assigned roles cannot be archived, and a user cannot archive their own membership. Restore returns recoverable records to Active.

Domain aggregates own lifecycle transitions, while application use cases own authorization and aggregate-specific safety rules such as protecting the current membership, system roles, grant boundaries, and assigned roles. PostgreSQL stores lifecycle evidence, APIs expose explicit archive and restore commands, and read models require an explicit lifecycle filter for non-active records.

Normal workspace and administration screens query Active records only. A dedicated, permission-aware Archive screen is the recovery boundary for Archived and Pending-deletion records across modules. Its registry maps each resource type to its read permission, manage permission, recoverable query, and valid commands. Adding a recoverable module therefore extends one registry instead of duplicating recovery UI and authorization logic across feature pages.

## Consequences

- Accidental deletion is recoverable without weakening the audit trail.
- Retention or regulatory purge can be added later as a separate privileged workflow rather than overloading normal delete behavior.
- A unique key remains reserved while a record is pending deletion so restoration cannot collide with a replacement record.
- Archive and Delete remain distinct states in the user interface: retirement intent is not presented as deletion.
