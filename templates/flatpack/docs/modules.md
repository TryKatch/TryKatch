# Flatpack modules

FlatpackApp is a modular monolith. Modules are full-stack business capabilities selected at installation or build time; infrastructure adapters such as SMTP or S3 are not modules by themselves.

## Stable seam

Backend modules implement `IFlatpackModule` from `FlatpackApp.Modules.Abstractions`. The host validates every descriptor before any module registration runs:

- IDs are stable lower-case kebab-case values.
- Versions use semantic versioning.
- IDs and declared dependencies are unique.
- Every required module is installed.
- Installed dependency graphs contain no cycles.
- Registration follows deterministic dependency order, including optional dependencies when they are installed.

`flatpack.modules.json` is the single application-owned source of truth. The generated backend registry is `src/FlatpackApp.Api/Modules/EnabledModules.cs`; never edit it or add assembly scanning. Existing module controllers carry `[FlatpackModule("module-id")]`; a catalog-aware MVC feature provider removes their HTTP surface when the module is disabled. New packages should implement `IFlatpackOrganizationEndpointContributor` from `FlatpackApp.Modules.AspNetCore`. The host gives contributors only a pre-authenticated `/api/v1` route group marked for organization resolution and RLS transaction setup, so module endpoints cannot opt out of the security kernel. The authenticated `GET /api/v1/modules` endpoint exposes the effective catalog and named extension points for diagnostics.

Modules may explicitly allowlist read-only or confirmed state-changing operations through `FlatpackAssistantToolDescriptor`. The OpenAPI pipeline marks module ownership and generates a provider-neutral strict tool contract. No endpoint becomes an AI tool merely because it exists. See [AI-assisted development](ai-assisted-development.md).

Web modules use `@flatpackapp/module-sdk`. Each definition owns lazy-loadable typed routes, navigation, named extension-point hosts, and extension contributions. Contributions have stable IDs, deterministic order, and optional permission gates. The catalog rejects duplicate contracts, unknown hosts, invalid overrides, missing dependencies, and cycles. `web/apps/web/src/modules.ts` is generated from the same catalog as the backend; application-owned visual overrides remain in `web/apps/web/src/module-overrides.ts` and are never overwritten. `null` disables a keyed contribution without editing its provider module.

Data-capable modules explicitly register `IApplicationModelContributor`. This keeps EF Core mapping behind the module seam: when a module is not enabled, its runtime entity model is not composed. Existing migrations and tables are retained; disabling a module is never a data-deletion operation.

Permission definitions may declare default grants for the standard organization role keys. Organization setup asks the aggregated catalog for those grants, so a new module can add a permission and its safe defaults without editing the organization directory. Owner remains the deliberate exception and receives every installed permission.

## Reference modules

Projects is the reference tier-spanning module:

```text
src/FlatpackApp.Domain/Projects
src/FlatpackApp.Application/Projects
src/FlatpackApp.Infrastructure/Projects
src/FlatpackApp.Infrastructure/Modules/ProjectsModule.cs
src/FlatpackApp.Api/Controllers/ProjectsController.cs
web/apps/web/src/features/projects
```

Its module registration owns the use cases, persistence adapter, EF model contributor, validator, permission provider, and default grants. Its web definition owns its route, navigation entry, and a named page extension point. Removing the module from both explicit registries removes its API controller, runtime entity model, services, permission definitions, role defaults, route, and navigation without changing shared shell or permission-catalog code. Historical database artifacts remain intact for safe re-enablement.

`FlatpackApp.Modules.GettingStarted` is the first package-shaped vertical slice. It deliberately stays small so the composition mechanics remain visible:

```text
src/FlatpackApp.Modules.GettingStarted/
  FlatpackApp.Modules.GettingStarted.csproj
  flatpack.module.json
  GettingStartedModule.cs
web/packages/module-getting-started/
  package.json
  src/index.tsx
```

It is a packable NuGet project paired with a workspace npm package. The manifest binds both packages to the same stable module ID, version, and dependency graph. The backend contributes a permission, a deep application service, and a minimal API endpoint through the host-owned security seam. The web package contributes a permission-gated page and navigation item, then extends `projects.list.after-table` without importing Projects implementation code.

To see disablement in action, run `dotnet run --project tools/FlatpackApp.ModuleTool -- module disable getting-started`. The endpoint, permission, route, navigation item, and Projects extension disappear together. Re-enable them with `module enable getting-started`. The operation uses atomic file replacement with rollback on failure, preserves module files and data, and refuses to disable a module required by another enabled module. Catalog tests prove dependency safety, registry drift detection, contribution collision detection, and coordinated backend/web disablement.

## Lifecycle commands

Run these commands from the generated solution root. The packaged tool command is `flatpack`; `dotnet run` works before installing it globally.

```bash
dotnet run --project tools/FlatpackApp.ModuleTool -- module list
dotnet run --project tools/FlatpackApp.ModuleTool -- module doctor
dotnet run --project tools/FlatpackApp.ModuleTool -- module generate
dotnet run --project tools/FlatpackApp.ModuleTool -- module disable getting-started
dotnet run --project tools/FlatpackApp.ModuleTool -- module enable getting-started
```

`doctor` checks strict manifest shape, host compatibility, stable identifiers, artifact paths, dependencies and cycles, duplicate permissions/routes/extensions/tools, extension targets, assistant-tool confirmation policy, and generated backend/web parity. CI runs it after every build. `generate`, `enable`, and `disable` use deterministic output, atomic file replacement, and rollback on handled failures; disabling is reversible and never removes data.

## Module rules

1. Depend on stable kernel interfaces, never another module's implementation.
2. Declare a hard dependency only when the module cannot operate without it. Optional integrations must degrade safely.
3. Contribute immutable permission definitions and safe standard-role defaults from the module; roles remain organization- or platform-owned records.
4. Organization data must include `OrganizationId`, application constraints, and a PostgreSQL RLS policy.
5. Writes go through application use cases and produce audit/outbox records in the same transaction where required.
6. Web routes, navigation, named hosts, and extensions are declared by modules. Extend another module only through a published point; do not reach into its private component tree.
7. Machine-owned registries produced by the module tool must remain deterministic and reviewed. CI rejects drift from `flatpack.modules.json`.
8. Disabling or removing module code never drops its data. Permanent purge is a separate, explicit retention operation.

## Adding a source module

1. Define the module-owned domain, application, infrastructure, HTTP, and web folders.
2. Add a backend entry implementing `IFlatpackModule`; use `IFlatpackOrganizationEndpointContributor` for its organization API and register only the module's own dependencies.
3. Add a versioned `flatpack.module.json` and register its path once in `flatpack.modules.json`.
4. Define permissions and optional standard-role defaults through an `IPermissionDefinitionProvider` owned by the module.
5. For persistent entities, register an `IApplicationModelContributor`; create migrations only from the fully enabled canonical registry and retain them when disabling a module.
6. Add a `FlatpackWebModule` definition and declare its import/export entrypoint in the manifest. Publish stable extension points for intended customization and permission-gate sensitive contributions.
7. Add dependency-graph, manifest-parity, permission, RLS, API, web, and disabled-module tests.
8. Run `flatpack module generate`, build OpenAPI, and regenerate the TypeScript client.

Development builds expose the raw OpenAPI 3.1 document at `/openapi/v1.json` and an interactive Scalar reference at `/docs`. `pnpm --dir web generate` also regenerates `docs/generated/assistant-contract.json`; CI rejects client or assistant-contract drift.

The first Flatpack CLI slice now owns list, doctor, deterministic generation, and dependency-safe enable/disable. Package acquisition, lockfile updates, safe upgrade, eject-to-source, unregister, and explicit purge-data workflows remain release work; external third-party module installation is not yet a production support promise.
