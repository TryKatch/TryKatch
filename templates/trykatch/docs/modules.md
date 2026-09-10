# Trykatch modules

TrykatchApp is a modular monolith. Modules are full-stack business capabilities selected at installation or build time; infrastructure adapters such as SMTP or S3 are not modules by themselves.

## Stable seam

Backend modules implement `ITrykatchModule` from `TrykatchApp.Modules.Abstractions`. The host validates every descriptor before any module registration runs:

- IDs are stable lower-case kebab-case values.
- Versions use semantic versioning.
- IDs and declared dependencies are unique.
- Every required module is installed.
- Installed dependency graphs contain no cycles.
- Registration follows deterministic dependency order, including optional dependencies when they are installed.

`trykatch.modules.json` is the single application-owned source of truth. The tool generates matching API, migrator, and React registries plus `trykatch.modules.lock.json`; never edit generated artifacts or add assembly scanning. Package entries retain an exact manifest SHA-256; workspace entries use a template-name-normalized SHA-256 so namespace replacement during `dotnet new -n` cannot invalidate an otherwise identical generated graph. Existing module controllers carry `[TrykatchModule("module-id")]`; a catalog-aware MVC feature provider removes their HTTP surface when the module is disabled. Organization packages implement `ITrykatchOrganizationEndpointContributor`; platform-administration packages implement `ITrykatchPlatformEndpointContributor` and must name a platform permission boundary. The host supplies the authenticated route group, so module endpoints cannot opt out of the security kernel. The authenticated `GET /api/v1/modules` endpoint exposes the effective catalog and named extension points for diagnostics.

Modules may explicitly allowlist read-only or confirmed state-changing operations through `TrykatchAssistantToolDescriptor`. The OpenAPI pipeline marks module ownership and generates a provider-neutral strict tool contract. No endpoint becomes an AI tool merely because it exists. See [AI-assisted development](ai-assisted-development.md).

Web modules use `@trykatchapp/module-sdk`. Each definition owns lazy-loadable typed routes, navigation, named extension-point hosts, and extension contributions. Contributions have stable IDs, deterministic order, and optional permission gates. The catalog rejects duplicate contracts, unknown hosts, invalid overrides, missing dependencies, and cycles. `web/apps/web/src/modules.ts` is generated from the same catalog as the backend; application-owned visual overrides remain in `web/apps/web/src/module-overrides.ts` and are never overwritten. `null` disables a keyed contribution without editing its provider module.

Data-capable modules declare their ownership and relations, then explicitly register `IApplicationModelContributor`. Organization entities implement `IOrganizationOwned`; the host supplies their named EF isolation filter and validates the entity shape, tenant-first index, declared relation, and policy contract. The migrator consumes the same generated ordered catalog as the API, rejects undeclared SQL tables, and inspects the live PostgreSQL schema after migration. A package may also implement `ITrykatchModuleMigrationContributor` for immutable forward-only SQL changes. PostgreSQL serializes those changes with an advisory transaction lock and records module, version, migration ID, checksum, and application time in `platform.module_migrations`. Editing or removing an applied migration fails closed. Existing migrations and tables are retained; disabling or unregistering a module is never a data-deletion operation. See [Module data isolation](module-data-isolation.md).

## Package lifecycle

- `register` adds reviewed workspace source and leaves it disabled.
- `install` verifies the allowlisted publisher, signed NuGet package, pinned package/provenance/SBOM hashes, exact paired NuGet/npm versions, compatible host range, collision-free contributions, and successful locked-graph regeneration before changing the workspace.
- `upgrade` only moves forward and refuses a package identity change.
- `disable` removes runtime/API/web composition while retaining code and data.
- `unregister` requires disablement, refuses dependents, removes unused package references, and keeps migration history/data.
- `eject` requires a matching checksum-verified source bundle and refuses every overwrite.
- There is deliberately no automatic `purge-data`; destructive data retirement needs a module-specific, reviewed runbook and separate authorization.

All workspace mutations (`register`, `eject`, `generate`, `enable`, `disable`, `install`, `upgrade`, and `unregister`) share one cross-process serialization boundary. The workspace path is made absolute and stripped of trailing separators before the lock identity is derived, so equivalent path spellings cannot create independent locks. Package mutations are also transactional: on validation or restore failure, the catalog, generated registries, manifests, project/package files, NuGet lockfiles, and pnpm lockfile are restored. A later operation always rereads the state written by the earlier operation; for example, a disable queued behind an upgrade cannot be overwritten by the upgrade's stale snapshot.

## Federation reference module

`TrykatchApp.Modules.Federation` and `@trykatchapp/module-federation` are registered but disabled by default. Enabling the module proves platform API and React contributions plus the module migration ledger. Its administration slice stores write-only client secrets through ASP.NET Core Data Protection, blocks unsafe issuer URLs, binds discovery metadata to the exact issuer, requires a successful current-configuration test before enablement, and requires disablement before retirement. It does not weaken or replace Trykatch cookie issuance, organization resolution, RLS, or permission enforcement.

Permission definitions may declare default grants for the standard organization role keys. Organization setup asks the aggregated catalog for those grants, so a new module can add a permission and its safe defaults without editing the organization directory. Owner remains the deliberate exception and receives every installed permission.

## Reference modules

Projects is the reference tier-spanning module:

```text
src/TrykatchApp.Domain/Projects
src/TrykatchApp.Application/Projects
src/TrykatchApp.Infrastructure/Projects
src/TrykatchApp.Infrastructure/Modules/ProjectsModule.cs
src/TrykatchApp.Api/Controllers/ProjectsController.cs
web/apps/web/src/features/projects
```

Its module registration owns the use cases, persistence adapter, EF model contributor, validator, permission provider, and default grants. Its web definition owns its route, navigation entry, and a named page extension point. Removing the module from both explicit registries removes its API controller, runtime entity model, services, permission definitions, role defaults, route, and navigation without changing shared shell or permission-catalog code. Historical database artifacts remain intact for safe re-enablement.

`TrykatchApp.Modules.Federation` is the package-shaped reference for an optional platform capability. It is paired with `@trykatchapp/module-federation`, registered in the catalog, and disabled by default. Its manifest binds the .NET and React entrypoints to the same stable module ID, version, dependency graph, permissions, routes, and extension contributions.

`modules/documents` is the independently packaged full-stack organization-data proof. It owns its entity, forward-only SQL migrations and forced-RLS policy, list/create/content-update/archive/restore/deletion-request use cases, permissions/default grants, audit/outbox events, assistant-tool declarations, React route/navigation/table/form, and extension contributions. HTTP policies are repeated through the host-owned module authorization seam inside every application use case. Documents use the recoverable Active/Archived/Deleted lifecycle; reasoned deletion requests require an archived record and stay restorable from the central Archive. The module composes only through stable module interfaces and `IOrganizationModuleData`; it has no reference to host Infrastructure, Domain, Application, API, or another module implementation.

The Projects module remains the enabled reference for organization-scoped domain behavior. Together, Projects and Federation demonstrate built-in and optional module shapes without adding tutorial-only navigation to generated applications.

## Lifecycle commands

Run these commands from the generated solution root. The packaged tool command is `trykatch`; `dotnet run` works before installing it globally.

```bash
dotnet run --project tools/TrykatchApp.ModuleTool -- module list
dotnet run --project tools/TrykatchApp.ModuleTool -- module doctor
dotnet run --project tools/TrykatchApp.ModuleTool -- module generate
dotnet run --project tools/TrykatchApp.ModuleTool -- module enable federation
dotnet run --project tools/TrykatchApp.ModuleTool -- module disable federation
```

`doctor` checks strict manifest shape, host compatibility, stable identifiers, artifact paths, dependencies and cycles, duplicate permissions/routes/extensions/tools, extension targets, assistant-tool confirmation policy, and generated backend/web parity. CI runs it after every build. `generate`, `enable`, and `disable` use deterministic output, atomic file replacement, and rollback on handled failures; disabling is reversible and never removes data.

## Module rules

1. Depend on stable kernel interfaces, never another module's implementation.
2. Declare a hard dependency only when the module cannot operate without it. Optional integrations must degrade safely.
3. Contribute immutable permission definitions and safe standard-role defaults from the module; roles remain organization- or platform-owned records.
4. Persistent modules must declare ownership and every relation. Organization entities implement `IOrganizationOwned`; migrations enable and force RLS and name a policy with both `USING` and `WITH CHECK` organization predicates.
5. Writes go through application use cases and produce audit/outbox records in the same transaction where required.
6. Web routes, navigation, named hosts, and extensions are declared by modules. Extend another module only through a published point; do not reach into its private component tree.
7. Machine-owned registries produced by the module tool must remain deterministic and reviewed. CI rejects drift from `trykatch.modules.json`.
8. Disabling or removing module code never drops its data. Permanent purge is a separate, explicit retention operation.

## Adding a source module

1. Define the module-owned domain, application, infrastructure, HTTP, and web folders.
2. Add a backend entry implementing `ITrykatchModule`; use `ITrykatchOrganizationEndpointContributor` for its organization API and register only the module's own dependencies.
3. Add a versioned `trykatch.module.json` and register its path once in `trykatch.modules.json`.
4. Define permissions and optional standard-role defaults through an `IPermissionDefinitionProvider` owned by the module.
5. For persistent entities, register an `IApplicationModelContributor`; create migrations only from the fully enabled canonical registry and retain them when disabling a module.
6. Add a `TrykatchWebModule` definition and declare its import/export entrypoint in the manifest. Publish stable extension points for intended customization and permission-gate sensitive contributions.
7. Add dependency-graph, manifest-parity, permission, RLS, API, web, and disabled-module tests.
8. Run `trykatch module generate`, build OpenAPI, and regenerate the TypeScript client.

Development builds expose the raw OpenAPI 3.1 document at `/openapi/v1.json` and an interactive Scalar reference at `/docs`. `pnpm --dir web generate` also regenerates `docs/generated/assistant-contract.json`; CI rejects client or assistant-contract drift.

The Trykatch CLI owns list, doctor, deterministic generation, dependency-safe enable/disable, checksum-gated package install and upgrade, unregister, and eject-to-reviewed-source. Permanent module-data purge and an external public module marketplace remain outside the support promise.
