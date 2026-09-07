# PostgreSQL security model

Flatpack uses one PostgreSQL database, three schemas, and two operational roles. The migration role owns schema objects. The runtime role can read and write only the required tables and must never receive `BYPASSRLS` or ownership.

Create roles outside application startup, with passwords supplied by your secret manager:

```sql
CREATE ROLE flatpack_migrator LOGIN NOINHERIT NOBYPASSRLS PASSWORD '<secret>';
CREATE ROLE flatpack_runtime LOGIN NOINHERIT NOBYPASSRLS PASSWORD '<secret>';
CREATE DATABASE flatpack OWNER flatpack_migrator;
```

Apply all three EF Core migration sets using the migrator connection:

```bash
dotnet tool restore
dotnet ef database update --context PlatformDbContext --project src/FlatpackApp.Infrastructure --startup-project src/FlatpackApp.Api
dotnet ef database update --context IdentityDbContext --project src/FlatpackApp.Identity --startup-project src/FlatpackApp.Api
dotnet ef database update --context ApplicationDbContext --project src/FlatpackApp.Infrastructure --startup-project src/FlatpackApp.Api
```

Then grant runtime access as the migration owner:

```sql
GRANT CONNECT ON DATABASE flatpack TO flatpack_runtime;
GRANT USAGE ON SCHEMA identity, platform, app TO flatpack_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, platform, app TO flatpack_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity, platform, app TO flatpack_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE flatpack_migrator IN SCHEMA identity, platform, app
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
