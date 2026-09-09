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

Permissions are immutable, code-defined capabilities. Each bounded application module explicitly registers an `IPermissionDefinitionProvider` containing stable keys and user-facing metadata. A singleton `IPermissionCatalog` aggregates providers and fails startup on duplicate keys, invalid key grammar, empty modules, or missing metadata.

Organization-owned custom roles remain database records containing permission keys. The catalog is not copied into a permissions table. The API publishes the catalog with a per-request `canGrant` boundary, and React renders that contract dynamically.

Authorization is deny-by-default:

1. The actor is authenticated.
2. The organization context resolves to an active membership.
3. A typed `RequirePermission` policy is satisfied.
4. The application use case repeats the permission check.
5. Role and membership mutations cannot grant permissions outside the actor's effective boundary.
6. Unknown or retired stored keys are filtered from effective access.
7. PostgreSQL RLS independently enforces organization isolation.

System roles remain immutable. Permission keys are never localized, wildcarded, or reused for different semantics.

## Consequences

- Adding a permission requires only a module-owned backend definition and its enforcement point; the catalog API, OpenAPI client, and editor adapt automatically.
- Optional modules register providers explicitly and do not require edits to a central React list.
- Role managers cannot assign Owner-equivalent authority unless they already hold every permission in that role.
- Roles can evolve without a database schema migration because grants remain stable strings.
- Removing or changing a permission requires an explicit migration strategy; keys must never be silently reinterpreted.

