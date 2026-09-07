# Recoverable archive pattern

Date: 2026-09-07

## Question

How should Flatpack provide a professional, maintainable recycle-bin experience across independently developed modules?

## Primary-source findings

- Microsoft Entra describes soft-deleted objects as unavailable for normal use while their properties and relationships are retained for restoration. This supports restoring records in place rather than copying them into a second archive table. [Microsoft Entra recoverability](https://learn.microsoft.com/en-us/entra/architecture/recoverability-tenant)
- Microsoft recommends treating deletion recovery as a deliberate operational capability and notes that object types have different recovery constraints. Flatpack therefore keeps resource-specific restore commands behind a shared Archive registry rather than pretending every aggregate restores identically. [Recoverability best practices](https://learn.microsoft.com/en-us/entra/architecture/recoverability-overview)
- Microsoft Fabric exposes recoverable items through a dedicated recycle-bin view with search, filtering, metadata, and restore. This supports a standalone Archive navigation area instead of mixing deleted records into normal working tables. [Fabric item recovery](https://learn.microsoft.com/en-us/fabric/admin/item-recovery)
- EF Core documents global query filters as the standard way to hide soft-deleted rows from normal queries while allowing explicit queries to include them. Flatpack applies explicit lifecycle predicates in its stores today; a future EF model-level filter can deepen that default without changing the API contract. [EF Core global query filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)

## Flatpack decision

1. Keep recoverable records in their original PostgreSQL tables so identifiers, relationships, RLS policies, and unique constraints remain authoritative.
2. Preserve two visible non-active states: `Archived` for intentional retention and `Pending deletion` for a reasoned disposal request. `Pending deletion` maps to the persisted `Deleted` state without claiming physical disposal has occurred.
3. Default feature queries to `Active`. Only the dedicated Archive requests the `Recoverable` scope.
4. Require normal read permission to discover an archived resource and manage permission to restore it.
5. Route restoration through the resource's application use case so aggregate-specific invariants and audit events still execute.
6. Require projects, memberships, and custom roles to be archived before deletion is requested. Invitations use Revoke or direct Delete instead of Archive.
7. Keep physical deletion behind a separately authorized retention-policy workflow rather than the normal application interface.
8. Register future recoverable modules in one frontend resource registry and implement their lifecycle transitions behind application interfaces.

## Rejected alternatives

- Moving deleted rows to parallel archive tables duplicates schemas, complicates foreign keys and RLS, and makes restoration fragile.
- Showing Active, Archived, and Deleted rows together on every feature page turns routine work tables into recovery consoles and spreads lifecycle logic throughout the UI.
- A generic database-level restore that bypasses use cases would miss aggregate-specific safety checks and audit semantics.
