# Extensible permission and role architecture

Date: 2026-09-07

## Decision summary

Trykatch should keep **permissions code-defined and roles organization-owned**. A permission is a stable application capability; a role is a named bundle of those capabilities. Modules contribute permission metadata to one validated runtime catalog, the API publishes that catalog, and the React role editor renders it. Adding a module or permission then changes one backend definition rather than backend validation, DTOs, and a hardcoded UI list separately.

This deliberately remains an in-process RBAC design for v1. It borrows the durable modeling boundaries of larger systems without introducing a Zanzibar-compatible authorization service, a policy language, or a separate deployment before Trykatch needs object-level relationship authorization.

## What the primary sources establish

- Google Cloud IAM models access as **principal + role + resource**. Permissions represent operations and are normally named `service.resource.verb`; users receive roles, not individual permissions, and roles are collections of permissions. It distinguishes provider-managed predefined roles from customer-managed custom roles and warns that broad basic roles are unsuitable for production. [Google Cloud IAM overview](https://docs.cloud.google.com/iam/docs/overview)
- Google publishes searchable role and permission metadata, including human-readable titles/descriptions, identifiers, lifecycle stage, and included permissions. Its custom-role UI filters permissions by service and type, and its API rejects permissions that are not supported in custom roles. Role identifiers are immutable, while an `etag` prevents concurrent edits from overwriting one another. [Google Cloud roles and permissions](https://docs.cloud.google.com/iam/docs/roles-overview), [custom roles](https://docs.cloud.google.com/iam/docs/creating-custom-roles), [roles and permissions index](https://docs.cloud.google.com/iam/docs/roles-permissions)
- AWS recommends least privilege, periodic removal of unused permissions, conditions that narrow access, and guardrails when permission administration is delegated. Its permissions-boundary model makes effective authority the intersection of granted permissions and a maximum boundary; the boundary limits authority but does not grant it. [AWS IAM security best practices](https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html), [permissions boundaries](https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies_boundaries.html)
- ASP.NET Core represents authorization as named policies containing requirements evaluated by handlers. `IAuthorizationPolicyProvider` can create parameterized policies dynamically instead of registering every permission separately, and Microsoft recommends a strongly typed authorization attribute for a custom provider. `IAuthorizationService.AuthorizeAsync` also supports resource-aware checks after the resource is loaded. [Policy-based authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0), [custom policy providers](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/custom-authorization-policy-providers?view=aspnetcore-10.0), [resource-based authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0)
- Microsoft advises tracking active tenant context on every request while validating authorization separately, and notes that database-backed permissions support fine-grained changes during a session whereas permissions embedded in claims wait for token reissuance. [Map requests to tenants](https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/considerations/map-requests), [multitenant identity](https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/approaches/identity), [multitenant identity and access](https://learn.microsoft.com/en-us/azure/well-architected/saas/identity-access)
- Zanzibar demonstrates why authorization should have a uniform model and a central check API. Its object/relation model separates stored grants from code-defined relation semantics, supports inspection of effective access, and emphasizes consistency after revocation. It is a useful future direction for object sharing or nested groups, not a justification for importing its distributed complexity into Trykatch v1. [Google Zanzibar paper](https://storage.googleapis.com/gweb-research2023-media/pubtools/5068.pdf)

## Recommended Trykatch model

### 1. Stable permission keys

Use lowercase, ordinal, non-localized identifiers with a documented grammar:

```text
<module>.<resource>.<action>
projects.project.read
projects.project.create
identity.member.invite
identity.role.update
audit.event.read
```

If preserving the existing two-part keys is more important than adopting three segments, keep them permanently; consistency and immutability matter more than the exact segment count. Never encode a role name, organization, UI label, route, or implementation class in a permission key. Never interpret wildcards in stored grants. Never reuse a retired key for new semantics.

Permission keys are data-contract identifiers. Display names and descriptions may change; identifiers should not. A semantic split creates new keys and a migration, not a rename in place. Keep deprecated definitions discoverable until all saved role grants are migrated, with `Deprecated` and `ReplacedBy` metadata.

### 2. A code-defined permission catalog

Replace the bare string set with immutable definitions such as:

```csharp
public sealed record PermissionDefinition(
    PermissionKey Key,
    string Module,
    string Resource,
    string Action,
    string DisplayName,
    string Description,
    PermissionRisk Risk,
    bool AssignableToCustomRoles = true,
    PermissionLifecycle Lifecycle = PermissionLifecycle.Active,
    PermissionKey? ReplacedBy = null,
    IReadOnlySet<PermissionKey>? Requires = null);
```

Each optional module exposes an explicit `IPermissionModule` (or a static descriptor) and registration code adds it deliberately; do not use assembly scanning. At startup, a singleton `IPermissionCatalog` flattens the registered modules and fails fast on duplicate keys, invalid grammar, missing metadata, unknown dependencies, or replacement cycles.

The database continues storing only `(RoleId, PermissionKey)`. The catalog is authoritative and code-owned; organizations own role names and their selected keys. This preserves the clean separation that Trykatch already has and avoids synchronizing a second permissions table on every deployment.

Expose a versioned endpoint such as `GET /api/v1/authorization/permissions` returning catalog metadata grouped by module/resource. React must consume this endpoint; it must not maintain `availablePermissions`. OpenAPI/Orval then keeps the client contract generated.

### 3. Safe custom-role mutation

The save use case should enforce all of these server-side:

1. The caller has `identity.role.manage` (or the retained equivalent).
2. Every submitted key exists, is active, and is assignable to custom roles.
3. Keys are distinct and required companion permissions are included or added explicitly with a returned explanation.
4. The caller can grant each submitted permission. The grantable set should be the intersection of the caller's effective permissions and a platform-defined delegation boundary; platform/organization Owner may have a documented exception. This is the Trykatch analogue of an AWS permissions boundary and prevents delegated administrators from escalating themselves.
5. System roles remain immutable, at least one active Owner remains, and a user cannot remove the last path to role administration.
6. Updates use a concurrency token (`xmin`, row version, or explicit version) and return `409 Conflict` for stale edits, following the role-`etag` pattern documented by Google Cloud.
7. The audit event records actor, organization, role, old/new keys, and correlation ID in the same transaction/outbox boundary. Authorization caches or sessions are invalidated immediately after a role or membership change.

Unknown or retired stored grants must fail closed. Return them in administrative diagnostics so an operator can migrate them; never silently reinterpret them.

### 4. Enforcement boundary

Create a single `PermissionRequirement(PermissionKey)` and a scoped handler backed by the resolved organization context. Microsoft explicitly warns against singleton authorization handlers that use EF Core. A dynamic policy provider should be purely syntactic: translate a namespaced policy such as `permission:projects.project.read` into an immutable requirement, while the handler performs request-specific evaluation. Do not put organization, membership, or database state in a cached policy. A strongly typed `RequirePermissionAttribute` or endpoint convention prevents string literals at controller call sites. [Authorization handler dependency injection](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/dependencyinjection?view=aspnetcore-10.0), [`IAuthorizationPolicyProvider` source](https://github.com/dotnet/aspnetcore/blob/main/src/Security/Authorization/Core/src/IAuthorizationPolicyProvider.cs)

Configure an authenticated fallback policy so endpoints without explicit metadata are not accidentally public; treat every `[AllowAnonymous]` use as security-sensitive. If multiple gates are placed in one policy, remember that ASP.NET Core evaluates separate requirements as AND, while multiple handlers for one requirement can satisfy it as OR. [Secure authorization data](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/secure-data?view=aspnetcore-10.0), [`AuthorizationHandlerContext` source](https://github.com/dotnet/aspnetcore/blob/main/src/Security/Authorization/Core/src/AuthorizationHandlerContext.cs)

Keep authorization at the application/use-case boundary as well, especially for commands invoked outside HTTP. Controllers may add declarative policies for fast rejection and OpenAPI visibility, but UI hiding and controller attributes are not the security boundary. Object-specific decisions should call `IAuthorizationService.AuthorizeAsync(user, resource, requirements)` after loading the resource. PostgreSQL RLS remains an independent organization-isolation backstop; it does not replace capability checks.

Evaluation is deny-by-default:

```text
authenticated actor
AND resolved active organization membership
AND exact active permission grant
AND delegation/resource constraints
AND PostgreSQL organization isolation
```

Do not add explicit deny semantics in v1. Deny-overrides-allow is powerful but makes multiple-role reasoning, support, and UI explanation substantially harder. Add it only with a concrete requirement and effective-access tooling.

Keep tokens limited to stable identity and coarse platform context; resolve organization grants from authoritative application data. If decisions are cached, key them by organization, actor, permission, optional resource, and a grant/policy version. Increment or invalidate that version on every role or membership mutation so revocation does not depend only on a cache TTL. Zanzibar's stale-ACL “new enemy” examples are the relevant warning here.

### 5. Professional role editor

The catalog makes the UI data-driven. Use a wide, responsive editor rather than a two-column list of raw strings:

- Role name and optional description at the top; show “System role” or “Custom role” clearly.
- Search by display name, description, resource, or exact key.
- Group permissions by module, then resource. A module section shows selected/total counts and may be collapsed.
- Render one permission per row with a checkbox, human label, one-line description, and the stable key as subdued technical metadata.
- Use explicit actions (`View`, `Create`, `Update`, `Delete`, `Invite`, `Assign`) as compact column labels or badges; do not turn raw identifiers into wrapped prose.
- Offer “Select module” only with a confirmation and show high-risk permissions distinctly. Avoid a global “select all” shortcut because it conflicts with least privilege.
- Keep a sticky footer with selected count, validation summary, Cancel, and Save. Warn when adding high-risk grants or removing permissions currently relied on by members.
- For edits, display impacted member count and a before/after summary. Surface `409` conflicts as “This role changed; review the latest version,” not as a generic failure.
- Use the same catalog for accessibility labels and help text. Keyboard order follows the visual order, group toggles expose `aria-expanded`, and every checkbox has a full accessible name.

This UI scales when modules add permissions: the new module registers definitions, the catalog endpoint exposes them, and the editor automatically gains a searchable group.

## Current Trykatch gaps and migration path

The existing implementation already has valuable foundations: organization-owned roles, immutable system roles, string grants persisted separately, server-side rejection of unknown keys, organization context resolution, audit events, and application-layer checks.

The main gaps are that `Permissions.All` has no metadata, the React app duplicates the permission list, role updates lack a concurrency token and grant boundary, and enforcement uses untyped string calls rather than ASP.NET Core requirements/policies.

Recommended delivery order:

1. Introduce `PermissionKey`, `PermissionDefinition`, module descriptors, and a validated catalog while preserving existing keys.
2. Add the catalog DTO/endpoint and replace the React constant with its generated query.
3. Redesign the permission picker around catalog groups, search, descriptions, selection counts, and risk indicators.
4. Add grant-boundary, dependency, last-owner, and duplicate-key validation plus concurrency control.
5. Add the ASP.NET Core requirement, handler, dynamic policy provider, and typed endpoint attribute/convention while retaining application-layer checks.
6. Add tests for catalog uniqueness, retired/unknown keys, privilege escalation, stale updates, last-owner protection, permission revocation, organization mismatch, and UI keyboard/accessibility behavior.

## Tradeoffs

- **Code catalog vs permissions table:** code definitions make reviews, deployments, and template modules deterministic. A database catalog enables runtime extensions but creates migration/version skew and lets data invent capabilities the application cannot enforce. Trykatch should choose code ownership for v1.
- **Module descriptors vs one central file:** explicit module descriptors let optional modules extend the catalog without editing a monolith, at the cost of a small startup composition layer.
- **Dynamic ASP.NET policies vs registering every policy:** the provider avoids repetitive registration and supports any catalog key, but its naming/parser logic must be strict and thoroughly tested because ASP.NET Core uses a single policy provider.
- **Coarse RBAC vs Zanzibar-style relationships:** organization RBAC is simpler and appropriate now. Relationship tuples become worthwhile only when requirements include per-project sharing, nested teams, delegated resource ownership, or cross-organization objects.
- **No explicit deny in v1:** simpler effective-permission reasoning and safer administration, but no exception rules. Resource constraints and delegation boundaries cover the immediate safety needs.
