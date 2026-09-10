# PostgreSQL security model

Trykatch uses three core PostgreSQL schemas and five operational roles. Modules may additionally declare reference or infrastructure resources under explicit access profiles. The migration role owns schema objects. Four mutually distinct runtime roles isolate organization, platform, identity, and outbox capabilities; none may receive role memberships, `BYPASSRLS`, or object ownership. See [Module data isolation](module-data-isolation.md) for the executable ownership and inspection contract.

Create the migration owner outside application startup, with its password supplied by your secret manager:

```sql
CREATE ROLE trykatch_migrator LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<migrator-secret>';
CREATE ROLE trykatch_org_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<organization-secret>';
CREATE ROLE trykatch_platform_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<platform-secret>';
CREATE ROLE trykatch_identity_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<identity-secret>';
CREATE ROLE trykatch_outbox_worker LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '<outbox-secret>';
CREATE DATABASE trykatch OWNER trykatch_migrator;
```

The bundled Compose stack uses the official PostgreSQL bootstrap administrator only on first initialization. Its init script creates the migration owner and four runtime roles as non-superusers, transfers database ownership to `trykatch_migrator`, and then leaves password rotation to the platform's secret-management workflow. Keep every password and connection in `.env.example` distinct. Managed production databases should provision the same roles through their normal infrastructure workflow instead.

PostgreSQL 18 stores data under a major-version-specific `PGDATA` directory and the official image exposes `/var/lib/postgresql` as its persistent volume root. Trykatch mounts the named volume at that root; do not change it back to the pre-18 `/var/lib/postgresql/data` target. Treat major-version upgrades as planned database migrations using `pg_upgrade` or a managed-provider upgrade workflow, never as an image-tag edit.

Run the dedicated one-shot migrator with the owner connection. It applies all three migration sets, grants the pre-created runtime role only data access, and verifies that the runtime role is neither a superuser nor able to bypass RLS. The migration role deliberately has no `CREATEROLE` authority:

```bash
ConnectionStrings__trykatchdb='<migrator connection>' \
Database__OrganizationRuntimeRole='trykatch_org_runtime' \
Database__PlatformRuntimeRole='trykatch_platform_runtime' \
Database__IdentityRuntimeRole='trykatch_identity_runtime' \
Database__OutboxWorkerRole='trykatch_outbox_worker' \
dotnet run --project src/TrykatchApp.Migrator
```

Production Compose runs this migrator to completion before starting the API. The API receives only the four scoped runtime connections; never expose `TRYKATCH_MIGRATOR_CONNECTION` to the API service. Applying migrations during an API replica's startup is deliberately unsupported because concurrent replicas make ownership and rollout ordering ambiguous.

Migration sets run in dependency order: `identity`, then `platform`, then `app`. Use expand-and-contract changes across releases: add compatible schema first, deploy code that can read both shapes, backfill under explicit monitoring, and remove obsolete schema only in a later release. Do not automatically roll back a partially applied production migration; stop the rollout, preserve evidence, and apply a reviewed forward repair unless the migration's documented rollback is proven safe.

Compose volumes are a local deployment convenience, not a backup system. Production owners must enable encrypted backups and point-in-time recovery in the database platform, monitor backup freshness, and perform restores into an isolated environment on a schedule. Record the achieved recovery point and recovery time before declaring the deployment production-ready.

For break-glass manual recovery, reproduce the scoped grants emitted by the current migrator; do not grant a runtime role across all schemas. Organization receives declared module-table CRUD, tenant/actor-scoped organization RBAC and directory access, audit/outbox append capabilities, and declared global read-only resources. Platform receives organization/placement control-plane CRUD and organization-scoped role/invitation provisioning, but no memberships, membership-role assignments, audit, or outbox access. Identity receives identity CRUD. The outbox worker receives only outbox SELECT/UPDATE capabilities. The migrator enumerates every non-system schema and removes runtime and `PUBLIC` privileges from its schemas, tables, sequences, and functions before rebuilding these exact profiles; it also removes database CREATE/TEMPORARY. Only the explicit PostgreSQL system-schema allowlist and anchored `pg_temp_<number>`/`pg_toast_temp_<number>` names are excluded; similarly named permanent schemas remain inspected. Undeclared persistent relations or user-schema functions fail validation, including objects in custom schemas. New application objects receive no blanket or default runtime grants.

The API starts a transaction for every organization-scoped request and sets `app.organization_id` and `app.actor_id` with transaction-local `set_config`. Tenant authorization and administration use the organization credential, never the platform credential. Tenant-owned rows fail closed when organization context is absent; narrowly scoped actor-directory and invitation-token read policies support workspace selection and invitation activation. Platform access is tied to the fixed platform role rather than a session bypass variable and does not bypass tenant RBAC policies. Never use the migrator connection from the running API.

Organization creation records a durable intent bound to the normalized slug, initiating platform actor, normalized administrator email, requested placement, and an idempotency identity. The platform transaction holds a slug-scoped advisory lock through organization preparation, placement provisioning, and invitation creation. A failed intent can only be retried with the same identity; an older organization without an intent cannot be claimed by retry, and a successful retry returns the one protected invitation token rather than issuing another invitation.

## OpenID Connect clients

Provision Authorization Code + PKCE or client-credentials clients through temporary configuration, then remove client secrets from the runtime environment. Password and implicit grants are never enabled.

```text
OpenIddict__Clients__0__ClientId=automation
OpenIddict__Clients__0__GrantType=client_credentials
OpenIddict__Clients__0__ClientSecret=<secret>
```

For a public PKCE client, use `authorization_code`, omit the secret, and add `OpenIddict__Clients__0__RedirectUris__0`.
