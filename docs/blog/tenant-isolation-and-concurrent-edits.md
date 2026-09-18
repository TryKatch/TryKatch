# Tenant isolation is more than a WHERE clause

## How Trykatch combines PostgreSQL RLS with safe concurrent edits

Editorial draft — researched 16 September 2026 and integrated 18 September 2026. The referenced generator safeguards are now included in the released preview.30 foundation. The local developer-experience validation below remains historical evidence, not a claim that existing applications or production deployments have already been upgraded or audited.

A multi-tenant application makes two promises: your workspace's data stays yours, and your work does not silently disappear when somebody else edits the same record.

Those promises sound related, but they require different safeguards.

PostgreSQL row-level security, or RLS, addresses which tenant's rows a database operation can access. Optimistic concurrency addresses whether an edit is based on a record that has since changed. Neither replaces the other.

In Trykatch, a tenant is an organization or workspace. Several organizations can share the same database and application tables. The tenant boundary is represented by an `OrganizationId` on organization-owned records, not by a separate schema for each customer.

## The failure that a tenant column cannot prevent

Imagine an invoices module serving two organizations: Acme and Beacon.

A developer writes:

```sql
SELECT * FROM app.invoices;
```

They intended to list invoices in the current workspace but forgot the tenant predicate. A tenant column alone does nothing to prevent the query from returning both organizations' records.

ORM query filters improve the default path, but raw SQL or an incorrectly disabled filter can escape that application convention. Trykatch adds a database check, so a forgotten predicate need not become a tenant data leak.

## Put the tenant boundary in PostgreSQL

The generated module migration uses this policy shape; `invoices` is an illustrative table name:

```sql
ALTER TABLE app.invoices ENABLE ROW LEVEL SECURITY;
ALTER TABLE app.invoices FORCE ROW LEVEL SECURITY;

CREATE POLICY invoices_organization_isolation ON app.invoices
  USING (
    "OrganizationId" =
      NULLIF(current_setting('app.organization_id', true), '')::uuid
  )
  WITH CHECK (
    "OrganizationId" =
      NULLIF(current_setting('app.organization_id', true), '')::uuid
  );
```

`USING` restricts existing rows available to reads, updates, and deletes. `WITH CHECK` restricts the proposed contents of inserted or updated rows. An update cannot take a visible Acme invoice and move it to Beacon. Rejected existing rows are normally invisible; rejected proposed contents produce an error. See PostgreSQL's [policy semantics](https://www.postgresql.org/docs/current/sql-createpolicy.html).

For this policy, absent or empty tenant context does not produce a true equality, so normal tenant access is denied. A query made under Acme's context sees Acme rows even if the application omitted its own tenant predicate. PostgreSQL documents the [row-security model and default-deny behavior](https://www.postgresql.org/docs/current/ddl-rowsecurity.html).

## Where the tenant context comes from

The database setting is not an authentication mechanism.

Trykatch's request pipeline extracts the authenticated actor and selected workspace, resolves an active membership in an active organization, and initializes the organization context. Generated use cases also check module permissions. Choosing a workspace does not, by itself, authorize its data.

The host then executes parameterized context-setting commands in the transaction used for organization data:

```sql
SELECT set_config('app.organization_id', @organizationId, true),
       set_config('app.actor_id', @actorId, true);
```

The final `true` makes the settings transaction-local. Tenant operations must use that same transaction and connection; an unrelated connection cannot inherit the context. Local settings expire at commit or rollback, instead of becoming persistent session state. See PostgreSQL's [configuration functions](https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADMIN-SET) and [SET LOCAL lifetime](https://www.postgresql.org/docs/current/sql-set.html).

This matters with connection pools. Npgsql normally resets connections between borrowers; pooling does not automatically leak tenant settings. Transaction-local context still narrows the lifetime and avoids depending on session-level tenant state. Reset-disabled pooling and still-open connection reuse require particular care. See [Npgsql's pooled connection reset](https://www.npgsql.org/doc/performance.html#pooled-connection-reset).

## Tenant isolation does not prevent lost edits

Now put Alice and Bob in Acme, with permission to edit the same invoice.

1. Both load the invoice with version token V1.
2. Alice changes its amount and saves. The record receives V2.
3. Bob submits his older form, still carrying V1.

Both users belong to the correct tenant, so RLS has no reason to reject Bob. Without a separate concurrency check, his stale form could overwrite Alice's change.

Trykatch's generated records carry an application-managed `Guid Version`, configured as an EF Core concurrency token. V1 and V2 above are labels for opaque UUIDs, not incrementing integers.

Generated update and lifecycle operations require an `expectedVersion`. They check that token against the loaded record, and successful state changes rotate the version. EF Core also includes the original token in its database update predicate. Schematically:

```sql
UPDATE app.invoices
SET "Amount" = @amount, "Version" = @newVersion
WHERE "Id" = @id AND "Version" = @originalVersion;
```

RLS constrains the eligible tenant rows independently of this predicate. If another committed edit changed the token, the stale update matches zero rows. EF Core raises `DbUpdateConcurrencyException`; the generated store translates it into a conflict, and the API returns HTTP 409 with code `stale_version`. This is the [EF Core optimistic concurrency pattern](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

The database predicate is essential: another writer could save after the application checks the token but before its own update executes. An in-memory comparison alone leaves that race open.

The generated editor keeps the user's unsaved inputs and offers an explicit refresh of the record/version. It does not silently merge edits or automatically retry an overwrite. Refreshing lets the user reconsider and submit again; it is not a guarantee that competing changes are compatible.

## The developer experience: make the safe path ordinary

Trykatch gives module developers a scoped persistence interface, `IOrganizationModuleData`, rather than handing them migration-owner credentials or the host's platform and identity contexts.

Organization-owned entities receive a centrally applied EF query filter. Module declarations identify owned relations. Generated migrations include the RLS policy and tenant-first index; generated mutation code includes version checks and conflict handling. Archive/recovery queries disable the named lifecycle filter without disabling organization isolation.

The schema inspector checks the installed database—not just migration source—for missing or widened policies, missing forced RLS, ownership drift, and unsafe runtime privileges. Migration ownership and runtime access are separate; organization, platform, identity, and outbox workloads have distinct role contracts.

These layers solve different mistakes. Membership and permissions authorize the request. The EF filter scopes normal queries. PostgreSQL enforces the row boundary. The concurrency token rejects stale edits. Inspection and integration tests check that the deployed schema and credentials preserve those assumptions.

## Be precise about the security claim

RLS is useful, not magical:

- Superusers and `BYPASSRLS` roles bypass policies. Owners normally bypass them unless `FORCE` applies, and schema owners can alter policies. Runtime roles must not carry those powers.
- Primary-key, uniqueness, and foreign-key integrity checks bypass row security and can reveal existence through errors. RLS alone does not enforce tenant-safe relationships. Tenant-scoped business uniqueness and relationships often need keys such as `(OrganizationId, InvoiceNumber)` and composite foreign keys that include `OrganizationId`.
- Policies that consult other tables can have concurrency subtleties. Do not assume that every permission change instantly revokes already-running work.

These limitations are documented in PostgreSQL's [row-security guidance](https://www.postgresql.org/docs/current/ddl-rowsecurity.html).

There is also an application trust boundary: PostgreSQL accepts [custom setting names](https://www.postgresql.org/docs/current/runtime-config-custom.html). A caller able to execute arbitrary SQL under the shared runtime role can change the tenant setting. Consequently, this design protects against omitted tenant predicates under trusted context; it is not a complete defense against SQL injection, stolen runtime credentials, or malicious in-process code. Parameterized SQL, restricted credentials, and trusted module code remain necessary.

Concurrency tokens do not replace idempotency, uniqueness constraints, or transaction isolation for multi-record business rules. Nor does shared-table RLS provide separate backups, separate compute, or physical isolation per customer. Trykatch's dedicated-placement seam currently fails closed unless an appropriate provisioning adapter exists; it should not be marketed as a shipped dedicated-database feature.

## Test the promises, not just the configuration

Trykatch includes real-PostgreSQL qualification using restricted runtime credentials. Its generated relation tests exercise missing-context denial, tenant-specific visibility, cross-tenant writes and declared attachment attempts. Local developer-experience acceptance also exercised competing generated CRUD writes: one success and one conflict.

That is stronger evidence than an in-memory test or a test run as the database superuser. It is still bounded evidence: the local validation report does not claim a complete release matrix, a browser end-to-end conflict test, or a production deployment audit.

See the [validation report](../developer-experience-validation.md) and [primary-source research note](../research/postgres-tenant-isolation.md).

The practical payoff is simple: module developers get tenant-aware queries and safe edit semantics by default, rather than rebuilding them for every screen. Customers get workspace boundaries and visible edit conflicts instead of accidental cross-tenant queries and silent overwrites.

## Implementation references for editorial review

- [Shared PostgreSQL decision](../adr/0002-shared-postgres-rls.md)
- [Module isolation and runtime role contract](../../templates/trykatch/docs/module-data-isolation.md)
- [Workspace authentication and membership resolution](../../templates/trykatch/src/API/Trykatch.Api/Security/OrganizationScopeMiddleware.cs)
- [Transaction-local organization context](../../templates/trykatch/src/API/Trykatch.Api/Security/OrganizationTransactionMiddleware.cs)
- [Generated tenant policy](../../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/Module.cs.tpl)
- [Generated version token](../../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/Entity.cs.tpl) and [EF token configuration](../../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/ModelContributor.cs.tpl)
- [Generated database conflict handling](../../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/Store.cs.tpl) and [HTTP conflict response](../../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/Endpoints.cs.tpl)
- [Real runtime-role tenant qualification](../../templates/trykatch/tests/Trykatch.IntegrationTests/GeneratedOrganizationIsolationTests.cs)
