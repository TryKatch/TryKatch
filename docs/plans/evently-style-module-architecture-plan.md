# Trykatch Evently-Style Module Architecture

## Summary

Restructure Trykatch into an Evently-style modular monolith with business modules grouped under `src/Modules`, five compiler-enforced projects per module, module-local frontend code and tests, and automated architecture validation.

Rename the `TrykatchApp.*` prefix to `Trykatch.*` while preserving existing behavior, database security, manifests, routes, migrations, and module tooling.

Outbox/inbox redesign, sagas, brokers, and messaging infrastructure are explicitly excluded.

## Target Structure

```text
src/
├── API/
│   ├── Trykatch.Api/
│   ├── Trykatch.AppHost/
│   └── Trykatch.Migrator/
│
├── Common/
│   ├── Trykatch.Application/
│   ├── Trykatch.Domain/
│   ├── Trykatch.Infrastructure/
│   ├── Trykatch.Identity/
│   ├── Trykatch.Modules.Abstractions/
│   ├── Trykatch.Modules.AspNetCore/
│   └── Trykatch.ServiceDefaults/
│
└── Modules/
    ├── Projects/
    │   ├── trykatch.module.json
    │   ├── Trykatch.Modules.Projects.Application/
    │   ├── Trykatch.Modules.Projects.Domain/
    │   ├── Trykatch.Modules.Projects.Infrastructure/
    │   ├── Trykatch.Modules.Projects.IntegrationEvents/
    │   ├── Trykatch.Modules.Projects.Presentation/
    │   └── Web/
    │
    ├── Documents/
    │   ├── trykatch.module.json
    │   ├── Trykatch.Modules.Documents.Application/
    │   ├── Trykatch.Modules.Documents.Domain/
    │   ├── Trykatch.Modules.Documents.Infrastructure/
    │   ├── Trykatch.Modules.Documents.IntegrationEvents/
    │   ├── Trykatch.Modules.Documents.Presentation/
    │   └── Web/
    │
    └── Federation/
        ├── trykatch.module.json
        ├── Trykatch.Modules.Federation.Application/
        ├── Trykatch.Modules.Federation.Domain/
        ├── Trykatch.Modules.Federation.Infrastructure/
        ├── Trykatch.Modules.Federation.IntegrationEvents/
        ├── Trykatch.Modules.Federation.Presentation/
        └── Web/

tests/
├── Trykatch.UnitTests/
├── Trykatch.IntegrationTests/
└── Modules/
    ├── Projects/
    │   ├── Trykatch.Modules.Projects.UnitTests/
    │   └── Trykatch.Modules.Projects.ArchitectureTests/
    ├── Documents/
    │   ├── Trykatch.Modules.Documents.UnitTests/
    │   └── Trykatch.Modules.Documents.ArchitectureTests/
    └── Federation/
        ├── Trykatch.Modules.Federation.UnitTests/
        └── Trykatch.Modules.Federation.ArchitectureTests/
```

Host-level integration tests remain centralized because they exercise module composition, HTTP, authentication, RLS, migrations, and shared infrastructure.

## Naming and Module Identity

Rename projects, assemblies, namespaces, folders, test projects, telemetry source names, and documentation consistently:

```text
TrykatchApp.Api                  → Trykatch.Api
TrykatchApp.Application          → Trykatch.Application
TrykatchApp.Domain               → Trykatch.Domain
TrykatchApp.Infrastructure       → Trykatch.Infrastructure
TrykatchApp.Identity             → Trykatch.Identity
TrykatchApp.Migrator             → Trykatch.Migrator
TrykatchApp.AppHost              → Trykatch.AppHost
TrykatchApp.ServiceDefaults      → Trykatch.ServiceDefaults
TrykatchApp.Modules.Abstractions → Trykatch.Modules.Abstractions
TrykatchApp.Modules.AspNetCore   → Trykatch.Modules.AspNetCore
```

`Application` and `AppHost` remain because they describe architectural roles. Only the redundant product prefix `TrykatchApp` is removed.

Stable module IDs remain unchanged:

```text
projects
documents
federation
```

The module ID is the machine identity used by manifests, dependencies, enablement, ownership validation, and tooling. Folder and assembly renames must never change it.

## Module Responsibilities

### Domain

Contains:

- Aggregates and entities.
- Value objects.
- Domain invariants.
- Domain errors.
- Internal domain events where already needed.
- Repository interfaces only when a real production and test adapter exist.

Domain must not depend on Application, Presentation, or Infrastructure.

### Application

Contains:

- Use cases.
- Application request and result types.
- Validation.
- Permission enforcement.
- Persistence interfaces required by use cases.
- Existing outbox calls without redesigning the outbox.

Application must not depend on HTTP or concrete persistence.

### IntegrationEvents

Contains the module's existing public cross-module event contracts.

Move `ProjectChanged` and `DocumentChanged` into their corresponding IntegrationEvents projects while preserving payload shape and serialized compatibility.

Do not introduce a new event bus, inbox, handler pipeline, or domain-event conversion mechanism in this scope.

### Presentation

Contains inbound adapters:

- HTTP endpoints.
- Request and response contracts.
- HTTP result translation.
- Endpoint metadata.

Presentation depends on Application and `Trykatch.Modules.AspNetCore`, never Infrastructure.

### Infrastructure

Contains:

- The module's single `IModule` implementation.
- Persistence adapters.
- EF mappings and model contributors.
- Module migration integration.
- Dependency registration.
- Module-specific external adapters.
- Composite NuGet packaging configuration.

Infrastructure composes the other projects belonging to its module.

## Dependency Rules

```text
Infrastructure ──▶ Presentation ──▶ Application ──▶ Domain
       │                                  │
       └──────────────────────────────────────▶ IntegrationEvents
```

Enforce:

- Domain references only approved stable Common abstractions.
- IntegrationEvents references only framework primitives or approved stable abstractions.
- Application references Domain and IntegrationEvents.
- Presentation references Application and module ASP.NET abstractions.
- Infrastructure may reference all projects belonging to its own module.
- A business module cannot reference another module's Domain, Application, Presentation, or Infrastructure.
- Cross-module references are permitted only to IntegrationEvents.
- The host references module Infrastructure entry points through the generated registry.
- Registration remains explicit and build-time generated; no runtime assembly scanning.

## Shared Security Kernel

Keep these responsibilities in Common rather than turning them into business modules:

- Identity and authentication.
- Organizations and organization resolution.
- RBAC and permission enforcement.
- PostgreSQL RLS.
- Auditing.
- Module manifest validation.
- Module catalog and activation.
- Organization-aware data access.
- Outbox implementation.
- Runtime database roles.
- Module package validation.

Preserve:

- `IModule`.
- `IOrganizationModuleData`.
- `IOrganizationOwned`.
- `IApplicationModelContributor`.
- `IModulePermissionAuthorizer`.
- Organization and platform endpoint contributor interfaces.
- Module descriptors, capabilities, permissions, resources, and assistant tools.

Their namespaces change from `TrykatchApp.*` to `Trykatch.*`; their behavior does not change.

## Business Module Migration

### Federation First

Use Federation as the structural proving module because it is already package-shaped and disabled by default.

Split its code into:

- Domain policies and value types.
- Application connection-management use cases.
- Presentation platform endpoints and DTOs.
- Infrastructure persistence, OIDC adapters, and module registration.
- An initially empty IntegrationEvents project with an assembly marker; do not invent events.

Move its frontend package and manifest under `src/Modules/Federation`.

Federation must remain disabled after migration.

### Documents Second

Split the current combined Documents implementation:

- Domain: document entity, lifecycle behavior, and domain errors.
- Application: create, list, update, and delete use cases.
- IntegrationEvents: existing `DocumentChanged`.
- Presentation: endpoints, request/response DTOs, and result mapping.
- Infrastructure: EF configuration, data adapter, migrations, and `DocumentsModule`.

Move its frontend package and manifest under `src/Modules/Documents`.

Preserve routes, operation IDs, permissions, extension points, assistant tools, table names, and RLS policy.

### Projects Last

Extract Projects from the shared projects:

- Project aggregate and domain behavior into Domain.
- Use cases, validation, permission checks, and persistence interface into Application.
- Existing `ProjectChanged` into IntegrationEvents.
- Project endpoints and HTTP contracts into Presentation.
- Store implementation, EF mapping, model contribution, and `ProjectsModule` into Infrastructure.
- Frontend feature into `src/Modules/Projects/Web`.

Preserve all HTTP routes, permissions, operation IDs, assistant-tool behavior, table mappings, RLS, auditing, and outbox behavior.

## Manifests and Packaging

Keep manifest schema version 1.

Each manifest points to its module Infrastructure project:

```json
{
  "id": "projects",
  "artifacts": {
    "dotnetProject": "src/Modules/Projects/Trykatch.Modules.Projects.Infrastructure/Trykatch.Modules.Projects.Infrastructure.csproj",
    "webPackage": "src/Modules/Projects/Web/package.json"
  },
  "entrypoints": {
    "dotnet": {
      "type": "Trykatch.Modules.Projects.Infrastructure.ProjectsModule"
    }
  }
}
```

Keep one NuGet package per module:

```text
Trykatch.Modules.Projects
Trykatch.Modules.Documents
Trykatch.Modules.Federation
```

Infrastructure acts as the composite packaging project. Each package contains:

```text
Trykatch.Modules.<Name>.Domain.dll
Trykatch.Modules.<Name>.Application.dll
Trykatch.Modules.<Name>.IntegrationEvents.dll
Trykatch.Modules.<Name>.Presentation.dll
Trykatch.Modules.<Name>.Infrastructure.dll
```

Do not introduce manifest schema v2 or multiple package identities per module.

Verify install, upgrade, unregister, and eject against the composite package.

## Database and Migration Compatibility

Keep one physical PostgreSQL database and host-owned `DbContext` composition.

Do not introduce per-module `DbContext` instances.

Preserve all applied migration IDs and database operations. Namespace-only source updates are permitted where required for compilation.

Documents continues using its module migration mechanism.

Projects' historical EF migrations remain in the host migration history. Add a checksum-stable baseline to the Projects module migration ledger before future Projects-owned migrations.

Do not rename tables, schemas, indexes, RLS policies, database roles, or migration-history entries.

Because namespaces are changing, preserve compatibility for any queued outbox rows that contain old CLR type names. Do not otherwise redesign the outbox.

## Architecture Tests

Use ArchUnitNET with the existing MSTest and Shouldly stack.

Add per-module tests covering:

- Valid layer dependency direction.
- Domain isolation.
- Presentation not referencing Infrastructure.
- IntegrationEvents not referencing implementation projects.
- Exactly one public sealed `IModule` implementation in Infrastructure.
- Implementation types internal where practical.
- Integration-event placement and naming.
- Manifest module ID and entry-point alignment.
- Entity-type metadata resolving to the moved type.

Add global architecture tests covering:

- No implementation references between business modules.
- Cross-module references target IntegrationEvents only.
- Host projects reference only module Infrastructure entry points.
- No runtime assembly scanning.
- Module IDs remain unique and lowercase kebab-case.
- `.csproj` project references match the intended graph.

Inspect project-reference XML directly in addition to compiled assemblies. Run architecture tests in Debug configuration.

Do not impose Evently's MediatR command/query conventions because Trykatch does not use them.

## Implementation Sequence

1. Run and record baseline builds, tests, module validation, template generation, and packaging.
2. Add architecture-test infrastructure and project-reference assertions.
3. Rename `TrykatchApp.*` to `Trykatch.*` across projects, assemblies, namespaces, tests, generated code, Dockerfiles, telemetry, and documentation.
4. Create the `src/API`, `src/Common`, `src/Modules`, and mirrored test organization.
5. Migrate Federation and validate the complete pattern.
6. Migrate Documents.
7. Migrate Projects.
8. Update manifests and generated module registration.
9. Add reusable composite-package configuration.
10. Update Docker build paths, solution membership, scripts, documentation, and CI.
11. Run the complete regression and generated-template acceptance suite.
12. Remove superseded project files and empty folders only after all references are verified.

## Test and Acceptance Scenarios

- Every project builds with warnings treated as errors.
- Existing unit and integration tests pass.
- Architecture tests reject deliberately invalid project references.
- API routes and operation IDs remain unchanged.
- Organization and platform permission enforcement remains unchanged.
- Antiforgery behavior remains unchanged.
- Organization query filters and PostgreSQL RLS remain active.
- Auditing and current outbox behavior remain unchanged.
- Module enablement and dependency ordering remain unchanged.
- Federation remains disabled.
- Documents continues requiring the `projects` module ID.
- Manifests pass module-doctor validation.
- Generated registries reference the new Infrastructure entry points.
- Composite packages contain all five assemblies.
- Packages restore and build from an isolated local feed.
- Install, upgrade, unregister, and eject workflows pass.
- The full Trykatch template generates and the generated application builds successfully.

## Explicit Non-Goals

This phase does not include:

- Inbox implementation.
- Outbox redesign or retry changes.
- New event-bus infrastructure.
- Domain-event pipeline redesign.
- Sagas or process managers.
- MassTransit, RabbitMQ, Kafka, or Quartz.
- MediatR.
- Per-module `DbContext` instances.
- xUnit or FluentAssertions migration.
- Runtime module discovery.
- Database schema redesign.

## Estimate

Production-grade implementation estimate:

```text
12–22 active agent-hours
Approximately 2–3 working days with build and test feedback
```

Suggested delivery checkpoints:

1. Naming and solution organization.
2. Dependency rules and architecture tests.
3. Federation migration.
4. Documents migration.
5. Projects migration.
6. Packaging, documentation, and full regression.

## Completion Criteria

The work is complete when:

- Solution Explorer reflects the Evently-style organization.
- No project or namespace retains the redundant `TrykatchApp` prefix.
- Projects, Documents, and Federation each have five correctly isolated projects.
- Module implementation is local to its module folder.
- Forbidden dependencies fail automatically.
- Module manifests and composite packages remain valid.
- Existing database, security, RLS, routes, permissions, frontend behavior, and tooling remain unchanged.
- All repository and generated-template acceptance tests pass.

## Delivery Status

The structural migration shipped in `0.1.0-preview.11`. The completion hardening described below is targeted for the next preview release.

The delivered structure uses explicit build-time composition and the product-neutral `IModule` contract. Projects, Documents, and Federation each own Domain, Application, IntegrationEvents, Presentation, Infrastructure, and Web artifacts inside their module directory. Application projects depend on explicit persistence and external-provider ports; EF Core and network adapters remain in Infrastructure. Presentation projects depend on Application and the shared ASP.NET module contract, never on module Domain or Infrastructure projects. The host references module Infrastructure entry points through generated build-time registration without runtime assembly scanning.

The shared module endpoint group applies antiforgery validation to every unsafe cookie-authenticated module operation. PostgreSQL-backed integration tests enumerate the module mutation surface and prove missing, invalid, and valid cookie-token behavior without unintended persistence. Separate authentication-boundary integration tests prove bearer-only and mixed-principal behavior with a deterministic test authentication scheme. Existing authentication, authorization, organization resolution, query filtering, runtime database roles, and PostgreSQL RLS tests remain part of the acceptance suite.

Architecture validation runs in Debug configuration and derives the expected five-project graph from module manifests. It enforces layer direction, blocks cross-module implementation references and runtime scanning, validates public module entry points, and includes a mutation test that proves an injected invalid project reference is rejected. Package CI discovers modules from their manifests, packs two versions of every composite module, verifies exactly five module assemblies, restores each through an isolated clean consumer, and exercises dependency-aware install, upgrade, and unregister against the actual built packages. Ejection uses each module's real five-project source tree, bound to the reviewed source manifest by a deterministic SHA-256 tree digest; tampering is rejected before workspace mutation.

Acceptance was verified with warnings treated as errors, 123 unit tests, 57 PostgreSQL integration tests, 20 architecture tests, 45 frontend unit and contract tests, three regular browser end-to-end tests, a production-generated browser journey, frontend type-check/build, documentation build with zero warnings, module-doctor validation, Federation enable/disable drift testing, CI change-detection testing, Compose validation, isolated composite-package consumption and lifecycle testing, and the complete generated-template matrix. The matrix includes arbitrary dotted and hyphenated product names and every supported feature permutation.
