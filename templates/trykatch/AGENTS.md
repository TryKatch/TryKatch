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

- Backend contract: `src/Common/Trykatch.Modules.Abstractions`
- Secured HTTP contribution: `src/Common/Trykatch.Modules.AspNetCore`
- Authoritative catalog: `trykatch.modules.json`
- Module lifecycle and diagnostics: `tools/Trykatch.ModuleTool`
- Generated backend registry: `src/API/Trykatch.Api/Modules/EnabledModules.cs`
- Web contract: `web/packages/module-sdk`
- Generated web registry: `web/apps/web/src/modules.ts`
- Application-owned web overrides: `web/apps/web/src/module-overrides.ts`
- Optional platform example: `src/Modules/Federation`
- Organization-data examples: `src/Modules/Projects` and `src/Modules/Documents`
- Module-local tests: `tests/Modules/<Module>`

Add a capability through module interfaces and a versioned manifest registered in `trykatch.modules.json`. Regenerate both explicit registries with the module tool. Do not use runtime assembly scanning or import another module's private implementation. Publish a named extension point when another module needs to contribute UI or behavior.

Each business module has Domain, Application, IntegrationEvents, Presentation, and Infrastructure projects plus a `Web` package. The host references only Infrastructure. Presentation never references Infrastructure; Domain and IntegrationEvents never reference implementation projects; another module may reference IntegrationEvents only. `Trykatch.ArchitectureTests` and module-local ArchUnitNET tests enforce these rules.

## API and AI workflow

1. Give every endpoint a stable operation ID.
2. Build the solution to regenerate OpenAPI 3.1.
3. Run `pnpm --dir web generate` to regenerate the TanStack client and assistant contract.
4. Opt an operation into AI tooling only through `TrykatchAssistantToolDescriptor` on its owning module.
5. Keep the assistant catalog deny-by-default. State-changing tools require explicit human confirmation and are re-authorized by the API.

## Verification

```bash
dotnet build Trykatch.slnx
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
dotnet test tests/Trykatch.UnitTests
dotnet test tests/Trykatch.ArchitectureTests
dotnet test tests/Modules/Projects/Trykatch.Modules.Projects.ArchitectureTests
dotnet test tests/Modules/Documents/Trykatch.Modules.Documents.ArchitectureTests
dotnet test tests/Modules/Federation/Trykatch.Modules.Federation.ArchitectureTests
dotnet test tests/Trykatch.IntegrationTests
pnpm --dir web generate
pnpm --dir web typecheck
pnpm --dir web test
pnpm --dir web build
```
