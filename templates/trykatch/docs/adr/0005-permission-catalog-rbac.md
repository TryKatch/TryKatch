# ADR 0005: Module-owned permission catalog and organization-owned roles

- Status: Accepted
- Date: 2026-09-07

## Context

Trykatch needs custom organization roles without duplicating permission strings across backend validation, OpenAPI, and React. Optional modules must be able to add capabilities without runtime assembly scanning or database-driven invention of permissions. Delegated role managers must not be able to grant authority they do not hold.

Primary guidance supports stable permission identifiers, least-privilege role bundles, server-side delegation boundaries, and policy-based enforcement:

- [ASP.NET Core policy-based authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0)
- [ASP.NET Core custom policy providers](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/iauthorizationpolicyprovider?view=aspnetcore-10.0)
- [AWS IAM least-privilege guidance](https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html)
- [Google Cloud IAM roles and permissions](https://docs.cloud.google.com/iam/docs/roles-overview)

## Decision

Permissions are immutable, code-defined capabilities. Each bounded application module explicitly registers an `IPermissionDefinitionProvider` containing stable keys, user-facing metadata, and optional default grants for the standard organization roles. A singleton `IPermissionCatalog` aggregates providers and fails startup on duplicate keys, invalid key grammar, unknown default-role keys, empty modules, or missing metadata. Organization setup consumes the aggregate defaults, so the directory does not know which permissions belong to optional modules. Ordinary capability authorization evaluates permissions rather than role names; protected ownership management is the explicit exception below.

Organization-owned custom roles remain database records containing permission keys. The catalog is not copied into a permissions table. The API publishes the catalog with a per-request `canGrant` boundary, and React renders that contract dynamically.

Authorization is deny-by-default:

1. The actor is authenticated.
2. The organization context resolves to an active membership.
3. A typed `RequirePermission` policy is satisfied.
4. Management use cases acquire the organization's transaction-scoped lock, then re-read the active actor membership, roles and permissions from the control-plane store. Earlier resolved context permissions are not authority proof.
5. Management requires authority over both the target's existing grants and proposed grants. Only a current Owner can manage Owner memberships or Owner invitations, or assign Owner, even when a custom role has identical permissions.
6. Unknown or retired stored keys are filtered from effective access.
7. PostgreSQL RLS independently enforces organization isolation.

System roles remain immutable. Permission keys are never localized, wildcarded, or reused for different semantics.

Every role, membership and invitation management mutation shares the advisory lock keyed by organization (`hashtextextended(organizationId, 734002)`) inside its actor-scoped, read-committed control-plane transaction. Invitation acceptance uses the same lock and re-reads the invitation before assigning membership. Suspending, demoting, archiving or deleting the final active Owner fails with `409 last_owner`; archived, deleted and suspended memberships do not count. Boundary denials use `403 forbidden` before any credentials or writes. Self-management remains prohibited even when another Owner exists. This deliberately limits permission-equivalent delegation to prevent a manager from removing the person who delegated authority.

Background callers must establish `IOrganizationDataScope` and an authenticated organization context before calling `OrganizationAdministration`. The store fails closed when the management transaction is missing. This lock does not make identity creation, control-plane writes, application audit writes and notification delivery a single atomic workflow; their commit/delivery guarantees are separate concerns.

## Consequences

- Adding a permission requires only a module-owned backend definition and its enforcement point; the catalog API, OpenAPI client, and editor adapt automatically.
- Optional modules register providers explicitly and do not require edits to a central React list.
- Delegated managers can grant ordinary roles only within their current permission boundary. Actual ownership cannot be manufactured by composing equivalent permission grants.
- Roles can evolve without a database schema migration because grants remain stable strings.
- Removing or changing a permission requires an explicit migration strategy; keys must never be silently reinterpreted.
