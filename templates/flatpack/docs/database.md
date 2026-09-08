# PostgreSQL security model

Flatpack uses one PostgreSQL database, three schemas, and two operational roles. The migration role owns schema objects. The runtime role can read and write only the required tables and must never receive `BYPASSRLS` or ownership.

Create the migration owner outside application startup, with its password supplied by your secret manager:

```sql
CREATE ROLE flatpack_migrator LOGIN NOINHERIT NOBYPASSRLS PASSWORD '<secret>';
CREATE DATABASE flatpack OWNER flatpack_migrator;
```

Run the dedicated one-shot migrator with the owner connection. It applies all three migration sets, creates or rotates the runtime role, grants only data access, and verifies that the runtime role is neither a superuser nor able to bypass RLS:

```bash
ConnectionStrings__flatpackdb='<migrator connection>' \
Database__RuntimeRole='flatpack_runtime' \
Database__RuntimePassword='<different 24+ character secret>' \
dotnet run --project src/FlatpackApp.Migrator
```

Production Compose runs this migrator to completion before starting the API. The API receives only `FLATPACK_RUNTIME_CONNECTION`; never expose `FLATPACK_MIGRATOR_CONNECTION` to the API service. Applying migrations during an API replica's startup is deliberately unsupported because concurrent replicas make ownership and rollout ordering ambiguous.

For break-glass manual recovery, the migrator performs the equivalent of:

```sql
GRANT CONNECT ON DATABASE flatpack TO flatpack_runtime;
GRANT USAGE ON SCHEMA identity, platform, app TO flatpack_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, platform, app TO flatpack_runtime;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity, platform, app TO flatpack_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA identity, platform, app
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO flatpack_runtime;
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
