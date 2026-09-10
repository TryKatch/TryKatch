# Module and tenant data-isolation implementation plan

## Objective

Make tenant placement and module data ownership enforceable properties of the Trykatch security kernel. A module must not be able to introduce persistent data without declaring who owns it, how it is isolated, and how that isolation is verified. Platform administrators may choose shared or dedicated application data storage when provisioning a tenant, while the user interface remains independent of the physical PostgreSQL topology.

This plan follows a build-time, trusted-module model. Authentication, organization resolution, permission enforcement, data placement, PostgreSQL access policy, audit, outbox, and module validation remain host-owned and non-replaceable.

## Product interface

Tenant creation presents one compact, accessible segmented control:

- **Shared database** — recommended default. Application rows share a database and are isolated by `OrganizationId`, EF Core query filters, PostgreSQL RLS, and runtime-role restrictions.
- **Dedicated database** — one application database per tenant. The control plane remains shared; tenant application data is provisioned and routed through a data-placement adapter.

This is a two-option choice, not an on/off switch. The interface uses native radio semantics, arrow-key navigation, visible focus, concise supporting text, and a clear recommended label. It must work in light, dark, system, custom shell colors, English, and French.

The choice is immutable after activation in the first release. Moving an existing tenant is a separate, audited migration workflow because it requires quiescing writes, copying data, validating counts and checksums, switching routing atomically, and retaining a rollback window.

The UI must never report a dedicated tenant as ready until the provisioning adapter has completed migrations, runtime-role grants, health verification, and routing registration. Failed provisioning is visible and retryable; it does not issue the owner invitation prematurely.

## Security-kernel interface

### Data ownership

Every module with the `data` capability declares exactly one default ownership class in its manifest:

- `organization` — customer data scoped to one organization.
- `platform` — control-plane data accessible only through platform permissions.
- `global` — deliberately shared reference data with documented read/write policy.
- `infrastructure` — host operational data such as migration history or outbox coordination.

Mixed-ownership modules declare each persistent resource explicitly. An undeclared table, entity, migration, or store is a validation error.

### Organization-owned entities

Organization-scoped EF entities implement a small `IOrganizationOwned` interface exposing `OrganizationId`. The application context applies a named `OrganizationIsolation` query filter from the resolved organization context. Lifecycle filters remain independently named so one cannot be disabled accidentally with the other.

The EF filter is a developer-safety default, not the authorization boundary. PostgreSQL RLS remains authoritative for shared organization tables, including raw SQL and future query paths.

### Model validation

At model construction and in CI, validation rejects any organization-owned entity that lacks:

- a non-null `OrganizationId` property;
- the `IOrganizationOwned` contract;
- the named organization query filter;
- an index beginning with `OrganizationId` for expected access paths;
- a manifest resource declaration matching its schema and table;
- a PostgreSQL isolation policy when placed in a shared database.

Platform, global, and infrastructure resources receive separate allowlisted rules instead of being silently exempted.

## Data-placement module

Define a deep host-owned module with one narrow interface. Callers request placement and receive an outcome; connection construction, secrets, migrations, retries, compensation, and provider details stay inside the implementation.

```csharp
public interface IOrganizationDataPlacement
{
    Task<OrganizationDataPlacementResult> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken);

    Task<OrganizationDataRoute> ResolveAsync(
        Guid organizationId,
        CancellationToken cancellationToken);
}
```

Adapters:

1. `SharedPostgresDataPlacement` routes to the shared application database and requires forced RLS.
2. `DedicatedPostgresDataPlacement` provisions and routes a tenant database. Its provider configuration may target the same PostgreSQL cluster, a deployment stamp, or a customer-managed PostgreSQL endpoint without changing the calling interface.
3. Test adapters simulate successful, failed, and delayed provisioning without network dependencies.

Database credentials are references to an external secret provider, never encrypted connection strings stored alongside application data. The control plane stores placement type, provider, region/stamp, database identifier, schema version, provisioning state, failure code, and timestamps.

Provisioning is an idempotent asynchronous workflow:

1. Persist a tenant in `Provisioning` state and write an outbox command.
2. Acquire a tenant-specific idempotency/advisory lock.
3. Create or select the data resource.
4. Apply host and enabled-module migrations in deterministic order.
5. Create least-privilege runtime access and verify ownership/RLS invariants.
6. Execute a read/write health probe.
7. register the route, mark the tenant `Ready`, and issue the owner invitation.
8. On failure, record a sanitized error and permit a safe retry. Compensation never destroys an existing data resource automatically.

## PostgreSQL enforcement

### Shared placement

Every organization-owned table uses:

- `ENABLE ROW LEVEL SECURITY`;
- `FORCE ROW LEVEL SECURITY`;
- an organization equality predicate in `USING`;
- the same organization equality predicate in `WITH CHECK`;
- a runtime role that is neither owner, superuser, nor `BYPASSRLS`.

### Runtime roles

Split the current broad runtime access into independently configured roles and connection pools:

- `trykatch_org_runtime` — organization data plane; cannot activate a platform bypass.
- `trykatch_platform_runtime` — platform control plane only.
- `trykatch_identity_runtime` — Identity, OpenIddict, and Data Protection operations only.
- `trykatch_outbox_worker` — the minimum read/update access needed for delivery.
- `trykatch_migrator` — schema owner used only by the one-shot migrator.

The `app.platform_admin` session variable is removed as an RLS bypass for organization connections. Platform operations use their own restricted connection and policies. Transaction-local organization and actor values remain mandatory on organization connections.

### Dedicated placement

Dedicated tenant databases retain application authorization, EF filters, least-privilege roles, and organization assertions even though physical separation reduces cross-tenant risk. This preserves defence in depth and allows the same modules to run in both placement modes.

## CI and qualification gates

### Schema isolation inspection

After migrations run against PostgreSQL, CI queries `pg_class`, `pg_namespace`, `pg_policy`, `pg_roles`, and `information_schema` and fails if:

- a declared organization table is absent;
- an organization table does not have both RLS flags enabled;
- a policy lacks `USING` or `WITH CHECK` organization enforcement;
- the runtime role owns relations or has superuser/`BYPASSRLS` privileges;
- an undeclared application table exists;
- manifest ownership and the migrated schema disagree.

### Generated isolation tests

For every organization-owned table, the qualification harness creates organizations A and B and verifies with the real runtime role that:

- A can read, insert, update, and delete A rows as permitted;
- A cannot observe or mutate B rows;
- B identifiers cannot be attached through foreign keys or join tables;
- missing organization context is default-deny;
- changing the transaction-local context cannot occur through the organization application path;
- EF queries apply the organization filter;
- raw SQL is still constrained by RLS.

The same behavioural suite runs against shared and dedicated adapters. Dedicated qualification provisions at least two databases and proves route separation, migration consistency, background processing, audit, backup discovery, and health checks.

## Trusted module distribution

SHA-256 remains an integrity check, but package authenticity additionally requires:

- signed NuGet packages with signature verification enabled;
- trusted-signer and package-source mapping configuration;
- pinned exact .NET and web package versions;
- build provenance/attestation and an SBOM attached to each release;
- an allowlisted publisher identity in the module registry;
- verification before package references, registries, or lock files are changed.

Modules execute inside the host process and are therefore trusted code. Trykatch does not describe third-party modules as sandboxed.

## Independent reference module

Create a third module, `documents`, outside the host projects and distribute it as paired .NET and frontend packages. Install it only with the Trykatch CLI. It proves the complete interface by contributing:

- organization-owned CRUD endpoints and EF mappings;
- module migrations and RLS declarations;
- permissions and default-role setup;
- React route, navigation, reusable table, and an extension slot;
- audit and outbox events;
- one read-only assistant tool and one confirmed mutating tool;
- shared and dedicated placement qualification tests.

No hand-edited API, migrator, router, navigation, permission, or OpenAPI registry change is allowed. Disabling the module removes its executable surfaces without deleting data. Re-enabling restores them. Uninstall/eject follows the existing reviewed lifecycle.

## Delivery sequence

### Phase 1 — Mandatory ownership contract

- Extend module manifests and runtime descriptors with ownership declarations.
- Introduce `IOrganizationOwned` and named EF filters.
- Add model and manifest validation.
- Bring projects, invitations, audit, and infrastructure tables into explicit classifications.

Exit: undeclared persistent data fails build/startup and existing tests remain green.

### Phase 2 — PostgreSQL proof and role separation

- Generate or author complete RLS policies for all shared organization resources.
- Split runtime roles and application connection paths.
- Add schema inspection and generated Testcontainers isolation tests.

Exit: CI proves default-deny cross-organization behaviour for every declared resource.

### Phase 3 — Placement control plane and UI

- Persist placement intent and provisioning lifecycle.
- Add the shared adapter and the dedicated PostgreSQL adapter.
- Add the accessible segmented control, status presentation, retry action, English/French translations, and audit events.
- Route HTTP requests, background work, health checks, migrations, audit, and outbox activity through the selected placement.

Exit: the UI cannot mark a tenant ready until the selected adapter passes qualification.

### Phase 4 — Supply-chain trust

- Sign releases, configure trusted signers, verify provenance and SBOMs, and fail closed in the CLI.

Exit: unsigned, incorrectly signed, unknown-publisher, mismatched, or vulnerable modules cannot mutate a workspace.

### Phase 5 — Independent full-stack proof

- Package, install, enable, qualify, disable, re-enable, eject, and upgrade the `documents` module.

Exit: no unrelated host source change is needed and both placement modes pass generated-application qualification.

## Release criteria

This work is complete only when:

- every Data module and persistent resource has a validated ownership declaration;
- shared placement has EF safety filters and forced PostgreSQL RLS;
- dedicated placement performs real provisioning and routing rather than storing a preference only;
- organization requests cannot activate platform database privileges;
- all module packages have verified publisher authenticity and integrity;
- the independent reference module passes the complete generated-application matrix;
- backup/restore, migration, failure-retry, telemetry, and tenant-isolation evidence exists for both placement modes;
- no unresolved high or critical security finding remains.

Until these gates pass, the tenant-placement control remains a preview capability and Trykatch remains a pre-release template.
