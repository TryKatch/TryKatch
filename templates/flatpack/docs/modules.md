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

The explicit registry is `src/FlatpackApp.Api/Modules/EnabledModules.cs`. Do not add assembly scanning. The authenticated `GET /api/v1/modules` endpoint exposes the effective catalog for diagnostics.

Web modules use `@flatpackapp/module-sdk`. Each definition owns lazy-loadable typed route and navigation contributions, and the catalog rejects duplicate module IDs, route IDs, route paths, navigation IDs, missing dependencies, and cycles. The explicit registry is `web/apps/web/src/modules.ts`.

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

Its module registration owns the use cases, persistence adapter, validator, and permission provider. Its web definition owns the route and navigation entry. Removing the module from both explicit registries removes its activation without changing shared shell or permission-catalog code.

## Module rules

1. Depend on stable kernel interfaces, never another module's implementation.
2. Declare a hard dependency only when the module cannot operate without it. Optional integrations must degrade safely.
3. Contribute immutable permission definitions from the module; roles remain organization- or platform-owned records.
4. Organization data must include `OrganizationId`, application constraints, and a PostgreSQL RLS policy.
5. Writes go through application use cases and produce audit/outbox records in the same transaction where required.
6. Web routes and navigation are declared by the module. Do not add feature-specific entries directly to the shell.
7. Machine-owned registries produced by future package tooling must remain deterministic and reviewed.
8. Disabling or removing module code never drops its data. Permanent purge is a separate, explicit retention operation.

## Adding a source module

1. Define the module-owned domain, application, infrastructure, HTTP, and web folders.
2. Add a backend entry implementing `IFlatpackModule` and register only the module's own dependencies.
3. Add the backend entry to `EnabledModules.All`.
4. Define permissions through an `IPermissionDefinitionProvider` owned by the module.
5. Add a `FlatpackWebModule` definition and register it in `web/apps/web/src/modules.ts`.
6. Add dependency-graph, permission, RLS, API, web, and disabled-module tests.
7. Build OpenAPI and regenerate the TypeScript client.

The planned Flatpack CLI will automate these edits for packaged NuGet and npm module pairs, including compatibility checks, lockfile updates, diagnostics, safe upgrades, and eject-to-source ownership.
