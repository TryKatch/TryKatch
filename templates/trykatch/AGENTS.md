# Trykatch agent guide

This file is the compact repository map for coding agents. The architectural decisions in `docs/adr` remain authoritative.

## Non-negotiable invariants

- `Organization` is the customer-facing term. Tenant is only the technical isolation mechanism.
- Organization-scoped requests cross authentication, workspace resolution, permission authorization, and a transaction that sets PostgreSQL `app.organization_id` and `app.actor_id` before data access.
- Browser code uses same-origin HttpOnly cookies and antiforgery. Never place access or refresh tokens in React storage.
- Platform authorization and organization authorization are separate permission catalogs.
- Business writes belong in focused application use cases, not controllers or React components.
- Generated OpenAPI clients and `docs/generated/assistant-contract.json` are machine-owned.

## Module map

- Backend contract: `src/TrykatchApp.Modules.Abstractions`
- Secured HTTP contribution: `src/TrykatchApp.Modules.AspNetCore`
- Authoritative catalog: `trykatch.modules.json`
- Module lifecycle and diagnostics: `tools/TrykatchApp.ModuleTool`
- Generated backend registry: `src/TrykatchApp.Api/Modules/EnabledModules.cs`
- Web contract: `web/packages/module-sdk`
- Generated web registry: `web/apps/web/src/modules.ts`
- Application-owned web overrides: `web/apps/web/src/module-overrides.ts`
- Package-shaped example: `src/TrykatchApp.Modules.Federation` and `web/packages/module-federation`
- Full data example: Projects across Domain, Application, Infrastructure, API, and React

Add a capability through module interfaces and a versioned manifest registered in `trykatch.modules.json`. Regenerate both explicit registries with the module tool. Do not use runtime assembly scanning or import another module's private implementation. Publish a named extension point when another module needs to contribute UI or behavior.

## API and AI workflow

1. Give every endpoint a stable operation ID.
2. Build the solution to regenerate OpenAPI 3.1.
3. Run `pnpm --dir web generate` to regenerate the TanStack client and assistant contract.
4. Opt an operation into AI tooling only through `TrykatchAssistantToolDescriptor` on its owning module.
5. Keep the assistant catalog deny-by-default. State-changing tools require explicit human confirmation and are re-authorized by the API.

## Verification

```bash
dotnet build TrykatchApp.slnx
dotnet run --project tools/TrykatchApp.ModuleTool -- module doctor
dotnet test tests/TrykatchApp.UnitTests
dotnet test tests/TrykatchApp.IntegrationTests
pnpm --dir web generate
pnpm --dir web typecheck
pnpm --dir web test
pnpm --dir web build
```
