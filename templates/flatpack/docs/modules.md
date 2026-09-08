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

The explicit registry is `src/FlatpackApp.Api/Modules/EnabledModules.cs`. Do not add assembly scanning. Module controllers carry `[FlatpackModule("module-id")]`; a catalog-aware MVC feature provider removes their HTTP surface when the module is disabled. The authenticated `GET /api/v1/modules` endpoint exposes the effective catalog and named extension points for diagnostics.

Web modules use `@flatpackapp/module-sdk`. Each definition owns lazy-loadable typed routes, navigation, named extension-point hosts, and extension contributions. Contributions have stable IDs, deterministic order, and optional permission gates. The catalog rejects duplicate contracts, unknown hosts, invalid overrides, missing dependencies, and cycles. The explicit registry and application-owned override map are in `web/apps/web/src/modules.ts`; `null` disables a keyed contribution without editing its provider module.

Data-capable modules explicitly register `IApplicationModelContributor`. This keeps EF Core mapping behind the module seam: when a module is not enabled, its runtime entity model is not composed. Existing migrations and tables are retained; disabling a module is never a data-deletion operation.

Permission definitions may declare default grants for the standard organization role keys. Organization setup asks the aggregated catalog for those grants, so a new module can add a permission and its safe defaults without editing the organization directory. Owner remains the deliberate exception and receives every installed permission.

## Reference module

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

## Module rules

1. Depend on stable kernel interfaces, never another module's implementation.
2. Declare a hard dependency only when the module cannot operate without it. Optional integrations must degrade safely.
3. Contribute immutable permission definitions and safe standard-role defaults from the module; roles remain organization- or platform-owned records.
4. Organization data must include `OrganizationId`, application constraints, and a PostgreSQL RLS policy.
5. Writes go through application use cases and produce audit/outbox records in the same transaction where required.
6. Web routes, navigation, named hosts, and extensions are declared by modules. Extend another module only through a published point; do not reach into its private component tree.
7. Machine-owned registries produced by future package tooling must remain deterministic and reviewed.
8. Disabling or removing module code never drops its data. Permanent purge is a separate, explicit retention operation.

## Adding a source module

1. Define the module-owned domain, application, infrastructure, HTTP, and web folders.
2. Add a backend entry implementing `IFlatpackModule`, mark owned controllers with `[FlatpackModule]`, and register only the module's own dependencies.
3. Add the backend entry to `EnabledModules.All`.
4. Define permissions and optional standard-role defaults through an `IPermissionDefinitionProvider` owned by the module.
5. For persistent entities, register an `IApplicationModelContributor`; create migrations only from the fully enabled canonical registry and retain them when disabling a module.
6. Add a `FlatpackWebModule` definition and register it in `web/apps/web/src/modules.ts`. Publish stable extension points for intended customization and permission-gate sensitive contributions.
7. Add dependency-graph, permission, RLS, API, web, and disabled-module tests.
8. Build OpenAPI and regenerate the TypeScript client.

The planned Flatpack CLI will automate these edits for packaged NuGet and npm module pairs, including compatibility checks, lockfile updates, diagnostics, safe upgrades, and eject-to-source ownership.
