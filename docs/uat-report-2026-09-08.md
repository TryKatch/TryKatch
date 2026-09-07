# Flatpack UAT report — 2026-09-08

## Scope

Platform role management parity and tenant-owner onboarding from platform administration into an isolated tenant workspace.

## Results

| Journey | Result | Evidence |
| --- | --- | --- |
| Platform role directory | Pass | Three protected built-in roles rendered in the reusable data table; rows expanded into readable capability summaries. |
| Custom platform role | Pass | Created, expanded, edited, and deleted a disposable role through the browser. The permission editor loaded its module catalog from the API. |
| Retry-safe role persistence | Pass | Live UAT exposed a user-transaction incompatibility with the Npgsql retry strategy. Role writes and serialized access changes now run through the EF execution strategy. |
| Tenant provisioning | Pass | Created `Northstar UAT` and issued a seven-day first-owner invitation without adding the platform operator as a workspace member. |
| New tenant identity | Pass | Invitation preview showed the destination workspace; the recipient created an account and accepted the invitation. |
| Workspace entry | Pass | Acceptance established the protected workspace context and opened `/overview` without exposing a tenant identifier in the URL. |
| Tenant authorization | Pass | The invited identity appeared as the only Northstar member with the built-in Owner role and access to workspace User Management. |
| Platform separation | Pass | The tenant owner had no platform role and could not enter `/dashboard`; the provisioning platform operator had no Northstar membership. |
| Installable template | Pass | Packed and installed `Flatpack.Templates.0.1.0.nupkg`, generated `Northstar.Sample`, and built the renamed solution with zero compiler warnings or errors. |

## Automated verification

- Canonical .NET solution build: pass, zero warnings and zero errors.
- .NET unit tests: 22 passed.
- PostgreSQL/Testcontainers integration tests: 4 passed, including runtime-role cross-organization RLS blocking.
- Generated API client: regenerated successfully from OpenAPI.
- Web/API-client tests: 15 passed.
- TypeScript project references: pass.
- Production Vite build: pass.

## Live review locations

- Platform tenant administration: `http://127.0.0.1:5173/dashboard/tenants`
- Tenant workspace: `http://127.0.0.1:5173/overview`

The disposable Northstar tenant remains in the local UAT database so both sides can be reviewed immediately.
