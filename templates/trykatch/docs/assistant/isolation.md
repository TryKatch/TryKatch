# Organization isolation and PostgreSQL RLS

## Authorization and row-level security

Authentication establishes the actor; workspace resolution establishes the active organization membership; permissions decide which operation is allowed. EF filters scope owned entities in the application. PostgreSQL row-level security (RLS) independently restricts organization-owned rows using app.organization_id. Policies are enabled and forced, with both USING for visible rows and WITH CHECK for allowed inserted/updated rows. The runtime connects with a non-owner role and must not bypass RLS.

No organization context means no organization rows are available through those policies. A record identifier from another organization must not grant access. RLS does not decide whether someone may edit a project or manage roles: operation/use-case authorization remains required. Platform, identity, global and infrastructure resources have separately declared ownership/access rules; not every table is an organization table.

## Connection pooling and concurrency

The host establishes transaction-local organization and actor settings before business data access. Transaction-local settings end with that transaction, preventing a pooled connection's previous organization setting from becoming the next request's scope. Set context and query in the same transaction/connection. Do not use session-wide tenant switches, mutable singleton organization state or parallel operations on one DbContext. Concurrent requests should have their own request-scoped context and transactions.

RLS is row isolation, not optimistic concurrency or a guarantee that conflicting business writes are serialized. Version checks, database constraints and appropriate transactions still protect competing updates. Test isolation against real PostgreSQL under the runtime role, including missing context and another organization's record; an in-memory test cannot prove the PostgreSQL policy.

## AI boundary

AI receives only results from module-owned authorized read adapters, executed in the caller's organization transaction. It never chooses organization or actor IDs and receives no elevated database principal. Conversation continuation is encrypted, authenticated, bounded and tied to actor, organization, membership and current access. Earlier answers cannot grant permission or prove that a record is still visible; current record facts require a fresh authorized read.
