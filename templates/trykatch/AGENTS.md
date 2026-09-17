# Trykatch agent guide

This file is the compact repository map for coding agents. The architectural decisions in `docs/adr` remain authoritative.

## Route the task

For first-time orientation, read [developer onboarding](docs/developer-onboarding.md) ([Français](docs/developer-onboarding.fr.md)). Inspect the actual checkout and installed module facts, distinguish existing behavior from examples, and begin read-only unless implementation is requested. Onboarding uses the developer's existing coding tool; it does not activate in-app AI Help.

Work from this application's root (the directory containing `Trykatch.slnx` and `trykatch.modules.json`). In the template repository, that root is `templates/trykatch/`. Paths in this guide are relative to that root; Markdown links in skills are relative to the skill file.

The application ships five skills in `.agents/skills/`. Use the matching skill when your agent supports discovery; otherwise open its `SKILL.md` directly and follow it. No global skill installation is required.

| Task | Entry point | Read when relevant |
| --- | --- | --- |
| Define a substantial feature before implementation | [trykatch-spec](.agents/skills/trykatch-spec/SKILL.md) | [Specification format](docs/development/specifications.md) |
| Create a new business module or workflow | [trykatch-build-module](.agents/skills/trykatch-build-module/SKILL.md) | [Backend guide](docs/development/backend.md), then [React guide](docs/development/frontend.md) if UI is requested |
| Change an existing module or add an extension | [trykatch-extend-module](.agents/skills/trykatch-extend-module/SKILL.md) | The owning module's code and [backend guide](docs/development/backend.md) |
| Review implementation against its request and architecture | [trykatch-review](.agents/skills/trykatch-review/SKILL.md) | [Authorization and isolation](docs/development/security.md) for access/data changes |
| Check a change, diagnose a failing check, or report readiness | [trykatch-verify](.agents/skills/trykatch-verify/SKILL.md) | [Verification matrix](docs/development/verification.md) |

Read only the guides needed for the change. A small fix does not need a new specification. When implementation is requested, continue through the relevant verification; do not stop after writing a plan. A request for a specification or review alone does not authorize implementation, commits, pushes, or publishing.

## Non-negotiable invariants

- `Organization` is the customer-facing term. Tenant is only the technical isolation mechanism.
- Organization-scoped requests cross authentication, workspace resolution, permission authorization, and a transaction that sets PostgreSQL `app.organization_id` and `app.actor_id` before data access.
- Browser code uses same-origin HttpOnly cookies and antiforgery. Never place access or refresh tokens in React storage.
- Platform authorization and organization authorization are separate permission catalogs.
- Business writes belong in focused application use cases, not controllers or React components.
- Generated OpenAPI clients and `docs/generated/assistant-contract.json` are machine-owned.
- CLI creation paths must show loading feedback through the shared `CliOperationProgress` presenter. Keep command output from interleaving with its spinner, use plain redirected logs, preserve failure diagnostics, and distinguish atomic module rollback from potentially partial project creation. Add progress tests and English/French docs when adding a creation command.

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

Each business module has Domain, Application, IntegrationEvents, Presentation, and Infrastructure projects. A module that declares the `web` capability also has a `Web` package; backend-only modules intentionally omit it. The host references only Infrastructure. Presentation never references Infrastructure; Domain and IntegrationEvents never reference implementation projects; another module may reference IntegrationEvents only. `Trykatch.ArchitectureTests` and module-local ArchUnitNET tests enforce these rules.

## API and AI workflow

1. Give every endpoint a stable operation ID.
2. Build the solution to regenerate OpenAPI 3.1.
3. If `web/package.json` exists, run `corepack pnpm --dir web generate` to regenerate the TanStack client and assistant contract. Backend-only applications build OpenAPI into the API project's `obj/openapi`; skip frontend commands.
4. Opt an operation into AI tooling only through `AssistantToolDescriptor` on its owning module.
5. Keep the assistant catalog deny-by-default. Its confirmation flag is metadata for a future adapter, not a running approval system. Product assistants require a separate adapter and runtime confirmation flow; see [AI-assisted development](docs/ai-assisted-development.md).
6. Use `module facts [module-id]` for validated installed ownership, permission, route, extension and assistant declarations plus source entrypoints. Treat declarations as metadata, not permission grants or proof an adapter exists.

## Verification

Choose checks from the [verification matrix](docs/development/verification.md). The following backend commands work from the application root; run module-local checks for each affected module, not only the examples listed here.

```bash
dotnet build Trykatch.slnx
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
dotnet test tests/Trykatch.UnitTests
dotnet test tests/Trykatch.ArchitectureTests
dotnet test tests/Modules/Projects/Trykatch.Modules.Projects.ArchitectureTests
dotnet test tests/Modules/Documents/Trykatch.Modules.Documents.ArchitectureTests
dotnet test tests/Modules/Federation/Trykatch.Modules.Federation.ArchitectureTests
dotnet test tests/Trykatch.IntegrationTests
```

Only when `web/package.json` exists:

```bash
corepack pnpm --dir web install --frozen-lockfile
corepack pnpm --dir web generate
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
```
