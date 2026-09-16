# Backend module work

Start with [module authoring](../modules.md) and the [local CLI reference](../../tools/Trykatch.ModuleTool/README.md) for the chosen mode. The installed source and `module create --help` are authoritative if an older example disagrees.

## Choose the implementation path

| Requirement | Starting point |
| --- | --- |
| New organization-owned editable records | `module create` with `--fields` |
| Explicit state transitions, guards, and review decisions | JSON blueprint, validate, then create |
| Existing module | Modify its source; use published seams for cross-module contributions |
| Platform/global ownership, unsupported relationships, cross-record rules | Manual module authoring or custom logic after a supported scaffold |

Run from the application root. Prefer the local CLI so its contracts match this application:

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
dotnet run --project tools/Trykatch.ModuleTool -- module create --help
```

For a new CRUD module, adapt the module/entity/resource names and fields to the request:

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module create Equipment \
  --entity EquipmentItem --resource equipment_items --ownership organization \
  --fields "name:string:required:max(120),dailyRate:decimal:required"
```

For a workflow, the shipped [shipment blueprint](../../blueprints/shipment-reception.json) illustrates the installed schema. Author a separate blueprint for a different feature. The local CLI reference describes its guards, supported assignment targets, bounded action inputs, and capability prerequisite. Arbitrary scripts, cross-module writes, relationships, and durable request-idempotency keys are outside blueprint v1. Do not weaken a business rule to make it fit the generator.

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module validate --blueprint blueprints/shipment-reception.json
dotnet run --project tools/Trykatch.ModuleTool -- module create ShipmentReceptions --blueprint blueprints/shipment-reception.json
```

Creation stages, registers, builds, and tests the module, with rollback on failure. Add `--with-web` for requested UI only when `web/package.json` exists. Do not edit the workspace concurrently with this transaction. Treat its success output as verification evidence; custom changes afterward need their own affected checks.

## Locate the owner

- [Projects](../../src/Modules/Projects/README.md) demonstrates organization CRUD. Read `ProjectUseCases.cs`, `ProjectsEndpoints.cs`, `ProjectsModelContributor.cs`, and `ProjectsModule.cs` in its corresponding layer projects.
- [Documents](../../src/Modules/Documents/README.md) demonstrates independently packaged object-backed behavior and authorization through shared module interfaces.
- [Federation](../../src/Modules/Federation/README.md) demonstrates an optional platform capability.
- [Module contracts](../../src/Common/Trykatch.Modules.Abstractions/TrykatchModule.cs) define registration, ownership, extensions, events, and assistant descriptors.

Keep domain invariants in Domain, orchestration/authorization in Application, HTTP binding in Presentation, and persistence/composition in Infrastructure. Another module may consume IntegrationEvents, never private implementation projects. [ADR 0011](../adr/0011-build-time-modular-monolith.md) and architecture tests define the boundaries.

## Changes after scaffolding

Module source is application-owned; registries and API clients are machine-owned. Use `module generate` when catalog or manifest composition changes. Rebuild the solution for OpenAPI changes; follow the [verification guide](verification.md) for client generation in React applications.

For persistence changes, declare every owned relation and use immutable forward-only migrations; never edit an applied module migration or drop data as a side effect of disablement. [Data isolation](../module-data-isolation.md) defines the EF contributor and migration contracts. Verify the resulting SQL and real PostgreSQL behavior.

Preserve decimal/64-bit integer string transports, UTC date-time handling, stable operation IDs and event contracts. Blueprint workflows require `expectedVersion` for changes; preserve their conflict behavior. Retained blueprints describe the initial/supported business contract and are not a source-overwrite mechanism.
