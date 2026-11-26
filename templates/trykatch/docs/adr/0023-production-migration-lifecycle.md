# ADR 0023 — Production migration lifecycle

## Decision

Keep the one-shot Migrator as the only deployment process that receives the database-owner connection. Use EF Core for the three host contexts and retain the existing checksummed SQL contract for optional modules. Serialize the complete deployment on one physical PostgreSQL connection. Require all five restricted runtime roles and verify the owner before production DDL. Start or promote API replicas only after the Migrator exits successfully.

## Current state and changes

| Question | Assessment |
| --- | --- |
| Who owns the model? | Identity and Platform have dedicated EF contexts. Application composes a model from enabled modules. Data-capable modules can also own immutable SQL. The shipped modules do not each need a DbContext. |
| How are changes discovered? | EF discovers compiled migrations for the selected context. Authors scaffold against the appropriate context and review its snapshot and SQL. For module-owned SQL, the author adds a numbered change to the module's migration contributor. Both hosts consume generated registries from the module manifests. |
| What runs first? | EF runs Identity, Platform, then Application. The module planner follows the resolved dependency graph, includes installed optional dependencies and sorts a module's migrations by ID. Catalog synchronization, object validation, grants and isolation inspection follow. |
| Where is history stored? | The contexts use EF's default shared history table. Keep migration IDs globally unique. Module SQL records the module ID, version, migration ID, checksum and timestamp in platform.module_migrations. Its key is module ID plus migration ID. Retained resource declarations are a separate catalog. |
| Can deployments race? | The base revision serialized module SQL and catalog transactions separately. The revised host also holds one advisory lock across EF, module SQL, validation and grants. All deployers must adopt the revised host before relying on the full-run guarantee. |
| What survives failure? | Earlier EF migrations may already have committed. Module SQL and its history share a transaction. Catalog synchronization and ordinary privilege reconciliation each have their own transaction. A failed verification blocks promotion. |
| Who can access what? | The owner login belongs to the deployment job. The API receives distinct organization, platform, identity, outbox and coordination credentials. Production preflight rejects unsafe or missing roles. The final inspection checks role attributes, runtime ownership, effective privileges, declared objects, function contracts and forced RLS policies. |
| Are upgrades compatible? | An upgrade can retain rows, history, identity keys and disabled-module data. Historical SQL must remain present and checksum-identical for enabled modules. Rolling schema compatibility requires a per-release test. Drain .NET 10 API replicas before .NET 11 starts writing to the shared Data Protection key ring. |

Separate EF history tables would require moving and reconciling production records. Revisit that decision if contexts must deploy independently or if a new context introduces an unavoidable ID collision. Module versions are audit information. A newer package version alone does not authorize a schema change.

## Failure and recovery

| Event | Durable state and operator action |
| --- | --- |
| Wrong owner, incomplete roles or unsafe runtime attributes | Production preflight stops before schema changes. Correct the deployment secrets or provision the intended roles, then rerun. |
| Another deployment holds the lock | The second job waits up to its configured command timeout. On timeout, inspect the active deployment and retry after it finishes. Closing the owning session releases the lock. |
| EF fails after one or more migrations | Inspect database state and EF history. Resolve the failed operation and roll forward. An operation that suppresses transactions requires its own recovery procedure. |
| Module SQL fails or the package edits a historical migration | The module batch rolls back. Restore the original immutable migration and append a corrective migration if needed. |
| Catalog or RLS verification fails | Keep the new API out of service. Preserve the database and inspect declarations, effective grants and policy expressions. Repair forward and rerun. An invalid host function may already have been quarantined. |
| A module is disabled or unregistered | Retain its history and data. Review access using the installed resource catalog. Disabling a module must never trigger a table drop. |

## Deployment phases and upgrade tests

1. **Current implementation.** Run the one-shot Migrator non-interactively, check its exit status and perform final catalog verification. PostgreSQL tests cover fresh creation, simultaneous deployers, rejected credentials, grant rollback and organization isolation. The platform upgrade also exercises existing encrypted keys.
2. **Next increment.** Expose read-only `plan`, `status` and `verify` commands alongside explicit `apply`. Reuse the existing EF metadata, module planner and catalog inspector. Emit ordered IDs, failed phase and stable exit codes suitable for CI, without connection strings or row contents.
3. **Release qualification.** Run the previous release against PostgreSQL Testcontainers, seed two offices, migrate forward with the candidate, run it twice and verify both row preservation and isolation with real runtime logins. Run old and new API binaries against the expanded schema during the overlap window. Preserve the failed database for diagnosis. A database reset invalidates upgrade evidence.
4. **Contract changes.** Introduce additive nullable columns or tables first, deploy compatible readers and writers, backfill and verify the result. Retire old binaries before a separately authorized maintenance operation. The standard deployment path must never automatically remove production data, tables or columns. Future EF and module SQL still require review for destructive statements.

The production role contract and invocation are documented in the [database runbook](../database.md).
