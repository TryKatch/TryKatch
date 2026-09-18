# PostgreSQL tenant isolation and optimistic concurrency

Verified against official PostgreSQL, Npgsql, and Microsoft documentation on 2026-09-16. These are platform guarantees and design implications, not evidence that this repository implements every safeguard.

## RLS activation and bypass

Creating policies is insufficient: `ALTER TABLE ... ENABLE ROW LEVEL SECURITY` activates them. With RLS enabled and no applicable policy, normal row access is denied. Table-level SQL privileges remain necessary; RLS does not grant them. Superusers and roles carrying `BYPASSRLS` always bypass RLS. Table owners normally bypass it too; `FORCE ROW LEVEL SECURITY` subjects the owner to policies but does not remove the other bypasses. `TRUNCATE` and `REFERENCES` are outside RLS. Therefore runtime credentials should not be superuser, `BYPASSRLS`, or casually share ownership/migration authority. [PostgreSQL row security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)

## Existing rows versus proposed rows

`USING` controls existing-row visibility and eligibility for update/delete: false or NULL normally hides the row without an error. `WITH CHECK` evaluates proposed inserted/updated contents: false or NULL produces an error. Explicitly checking the tenant in both places makes write intent clear and prevents an allowed existing row from being reassigned across tenants. For `ALL` and `UPDATE` policies, an omitted `WITH CHECK` inherits `USING`. Policies are permissive by default and combine with OR; restrictive policies combine with AND, but require at least one applicable permissive policy granting access. Additional permissive policies can therefore widen an otherwise tenant-scoped policy. [PostgreSQL CREATE POLICY](https://www.postgresql.org/docs/current/sql-createpolicy.html)

## Tenant context must share the transaction

`SET` defaults to session scope and, after a successful transaction commit, persists until changed or the session ends. `SET LOCAL` expires at commit or rollback; outside an explicit transaction block it warns and has no effect. Rolling back to an earlier savepoint can also cancel the setting. A local value temporarily overrides, rather than erases, an existing session value. [PostgreSQL SET](https://www.postgresql.org/docs/current/sql-set.html)

`set_config('app.tenant_id', value, true)` has transaction-local scope; `false` has session scope. `current_setting(name, true)` returns NULL if the setting is absent. A policy can handle absent context by denying access rather than allowing a fallback tenant. [Configuration functions](https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADMIN-SET)

Inference: start a transaction, parameterize and set its tenant context, then execute all tenant operations on that same connection/transaction. Setting context in a separate autocommitted statement does not establish context for later statements. Npgsql's documented transaction pattern holds one connection across commands, and parameters keep data separate from SQL. [Npgsql basic usage](https://www.npgsql.org/doc/basic-usage.html)

## Pooling and the trust boundary

Npgsql normally resets pooled connection state before its next use, preventing state leakage between borrowing cycles; this reset can be disabled with `No Reset On Close`. Therefore “pooling leaks tenant settings” is too broad. Session-scoped tenant values are risky when connections are reused without reset or when a still-open connection changes tenants. Transaction-local context minimizes that lifetime; test actual driver/pool configuration and rollback paths. [Npgsql pooled connection reset](https://www.npgsql.org/doc/performance.html#pooled-connection-reset)

PostgreSQL accepts custom two-part setting names as placeholders. Inference: an application-written tenant setting is not independently authenticated by the database; a caller able to issue arbitrary SQL under that role can change it. RLS based on that setting protects against omitted tenant predicates, not every compromise of application SQL execution. [PostgreSQL customized options](https://www.postgresql.org/docs/current/runtime-config-custom.html)

## RLS is not side-channel-proof

Unique/primary-key and foreign-key integrity checks bypass row security. PostgreSQL explicitly warns that these checks can leak information through covert channels. Thus RLS cannot justify claiming that every cross-tenant operation is invisible or impossible; schema constraints and error handling need separate review. Policies consulting other tables also require care around races. [PostgreSQL row security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)

## Stale EF Core writes

A configured concurrency token is tracked when loaded and its original value is included in update/delete matching. If a concurrent change means zero rows match, `SaveChanges` throws `DbUpdateConcurrencyException`; application code must resolve the conflict. Insert unique violations generally produce provider-specific exceptions instead. Application-managed tokens must be regenerated when persisting relevant changes. [EF Core concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)

Npgsql supports a `uint` concurrency property mapped to PostgreSQL's `xmin` via `[Timestamp]` or `.IsRowVersion()`. This detects stale writes; it does not replace tenant authorization. [Npgsql concurrency tokens](https://www.npgsql.org/efcore/modeling/concurrency.html)
