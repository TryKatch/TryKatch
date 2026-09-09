# PostgreSQL security model

Trykatch uses one PostgreSQL database, three schemas, and two operational roles. The migration role owns schema objects. The runtime role can read and write only the required tables and must never receive `BYPASSRLS` or ownership.

Create the migration owner outside application startup, with its password supplied by your secret manager:

```sql
CREATE ROLE trykatch_migrator LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<migrator-secret>';
CREATE ROLE trykatch_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<runtime-secret>';
CREATE DATABASE trykatch OWNER trykatch_migrator;
```

The bundled Compose stack uses the official PostgreSQL bootstrap administrator only on first initialization. Its init script creates both operational roles as non-superusers, transfers database ownership to `trykatch_migrator`, and then leaves password rotation to the platform's secret-management workflow. Keep `TRYKATCH_POSTGRES_ADMIN_PASSWORD`, `TRYKATCH_MIGRATOR_PASSWORD`, and `TRYKATCH_RUNTIME_PASSWORD` distinct. Managed production databases should provision both roles through their normal infrastructure workflow instead.

PostgreSQL 18 stores data under a major-version-specific `PGDATA` directory and the official image exposes `/var/lib/postgresql` as its persistent volume root. Trykatch mounts the named volume at that root; do not change it back to the pre-18 `/var/lib/postgresql/data` target. Treat major-version upgrades as planned database migrations using `pg_upgrade` or a managed-provider upgrade workflow, never as an image-tag edit.

Run the dedicated one-shot migrator with the owner connection. It applies all three migration sets, grants the pre-created runtime role only data access, and verifies that the runtime role is neither a superuser nor able to bypass RLS. The migration role deliberately has no `CREATEROLE` authority:

```bash
ConnectionStrings__trykatchdb='<migrator connection>' \
Database__RuntimeRole='trykatch_runtime' \
dotnet run --project src/TrykatchApp.Migrator
```

Production Compose runs this migrator to completion before starting the API. The API receives only `TRYKATCH_RUNTIME_CONNECTION`; never expose `TRYKATCH_MIGRATOR_CONNECTION` to the API service. Applying migrations during an API replica's startup is deliberately unsupported because concurrent replicas make ownership and rollout ordering ambiguous.

Migration sets run in dependency order: `identity`, then `platform`, then `app`. Use expand-and-contract changes across releases: add compatible schema first, deploy code that can read both shapes, backfill under explicit monitoring, and remove obsolete schema only in a later release. Do not automatically roll back a partially applied production migration; stop the rollout, preserve evidence, and apply a reviewed forward repair unless the migration's documented rollback is proven safe.

Compose volumes are a local deployment convenience, not a backup system. Production owners must enable encrypted backups and point-in-time recovery in the database platform, monitor backup freshness, and perform restores into an isolated environment on a schedule. Record the achieved recovery point and recovery time before declaring the deployment production-ready.

For break-glass manual recovery, the migrator performs the equivalent of:

```sql
GRANT CONNECT ON DATABASE trykatch TO trykatch_runtime;
GRANT USAGE ON SCHEMA identity, platform, app TO trykatch_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, platform, app TO trykatch_runtime;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity, platform, app TO trykatch_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA identity, platform, app
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO trykatch_runtime;
```

The API starts a transaction for every organization-scoped request and sets `app.organization_id` and `app.actor_id` with transaction-local `set_config`. RLS fails closed when either variable is absent. Never use the migrator connection from the running API.

## OpenID Connect clients

Provision Authorization Code + PKCE or client-credentials clients through temporary configuration, then remove client secrets from the runtime environment. Password and implicit grants are never enabled.

```text
OpenIddict__Clients__0__ClientId=automation
OpenIddict__Clients__0__GrantType=client_credentials
OpenIddict__Clients__0__ClientSecret=<secret>
```

For a public PKCE client, use `authorization_code`, omit the secret, and add `OpenIddict__Clients__0__RedirectUris__0`.
