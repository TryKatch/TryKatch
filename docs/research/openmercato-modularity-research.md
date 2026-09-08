# OpenMercato modularity and pluggable-assistant research

Date: 2026-09-08

## Decision summary

Flatpack should adopt OpenMercato's strongest architectural idea: a **contract-driven modular monolith composed at build time**. It should not copy OpenMercato's TypeScript implementation or imply that arbitrary code can be safely hot-loaded at runtime.

The recommended Flatpack design is an explicit module registry backed by versioned contracts. A module contributes its own application use cases, infrastructure adapters, EF Core migrations, permissions, HTTP endpoints, health checks, background work, React routes/navigation/widgets, and optional AI agents/tools. The host validates dependency and permission graphs before startup, while PostgreSQL RLS and the existing organization context remain non-negotiable isolation boundaries.

This is compatible with Flatpack's Clean Architecture and its existing prohibition on runtime service scanning: discovery can be explicit or source-generated, deterministic, testable, and Native AOT-friendly.

## What OpenMercato actually does

### Full-stack modules with build-time discovery

OpenMercato describes itself as a modular monolith: one deployable application whose modules isolate UI, API, and schema changes. Its application explicitly lists enabled modules in `src/modules.ts`; generators then discover conventional module files and produce registries. [Official architecture](https://www.openmercato.com/architecture), [module configuration source](https://github.com/open-mercato/open-mercato/blob/main/apps/mercato/src/modules.ts), [module discovery reference](https://github.com/open-mercato/open-mercato/blob/main/.ai/docs/module-development.md)

A module is a vertical slice. Conventionally discovered contributions include frontend and backend pages, API routes, DI registration, ACL features, tenant setup, entities and migrations, events/subscribers, workers, notifications, UI widgets and component overrides, generator plugins, and AI agents/tools. Routes and pages are derived from their module-relative paths rather than being wired into one central router by hand. [Module authoring guide](https://github.com/open-mercato/open-mercato/blob/main/.ai/docs/module-development.md#auto-discovery-paths), [scanner source](https://github.com/open-mercato/open-mercato/blob/main/packages/cli/src/lib/generators/scanner.ts)

The module's `index.ts` supplies stable metadata such as ID/name, title, version/description, and whether its source may be ejected. Published modules keep source under `src/modules/<moduleId>` and compiled output under `dist/modules/<moduleId>`. [Module package resolver](https://github.com/open-mercato/open-mercato/blob/main/packages/cli/src/lib/module-package.ts), [ejectable example manifest](https://github.com/open-mercato/official-modules/blob/main/packages/test-package/src/modules/test_package/index.ts)

### Named extension points instead of core edits

OpenMercato's Universal Module Extension System (UMES) defines named, typed extension points and generated registries. Its implemented surfaces include menu and widget injection, DataTable columns/actions/filters, CrudForm fields, response enrichers, API interceptors, component replacement, and event bridging; the broader design also covers mutation guards, command interceptors, detail bindings, and query extensions. Contributions have deterministic IDs, priorities, feature gates, and conflict diagnostics. [UMES specification](https://github.com/open-mercato/open-mercato/blob/main/.ai/specs/implemented/SPEC-041-2026-02-24-universal-module-extension-system.md)

The real official InPost module demonstrates this boundary: it registers a carrier adapter through DI, contributes feature IDs, assigns defaults during tenant setup, and places configuration/tracking UI into host-defined widget slots without editing the shipping or sales modules. [InPost module](https://github.com/open-mercato/official-modules/tree/main/packages/carrier-inpost/src/modules/carrier_inpost), [widget mapping](https://github.com/open-mercato/official-modules/blob/main/packages/carrier-inpost/src/modules/carrier_inpost/widgets/injection-table.ts)

OpenMercato also has a unified override map in the application's module registry. A consuming app can replace or disable individual routes, workers, widgets, notifications, interceptors, enrichers, guards, CLI commands, ACL features, DI registrations, and AI agents/tools without modifying the provider module; `null` is the disable convention for keyed contracts. [Override configuration example](https://github.com/open-mercato/open-mercato/blob/main/apps/mercato/src/modules.ts#L19-L66), [AI override implementation](https://github.com/open-mercato/open-mercato/blob/main/packages/ai-assistant/src/modules/ai_assistant/lib/ai-overrides.ts)

### Module-owned data and migrations

Each enabled module owns its MikroORM entities and migration directory. The migration runner iterates enabled modules and maintains a separate migration-history table per module. [Migration runner](https://github.com/open-mercato/open-mercato/blob/main/packages/cli/src/lib/db/commands.ts#L336-L420)

Cross-module ORM relationships and direct business-logic imports are forbidden. The prescribed seams are events for write-side reactions, widgets plus response enrichers for read/UI composition, and scalar foreign-key IDs plus snapshots or separate extension entities for data links. Optional peers must be resolved defensively and degrade when absent. [Repository architecture rules](https://github.com/open-mercato/open-mercato/blob/main/AGENTS.md#architecture), [cross-module coupling rules](https://github.com/open-mercato/open-mercato/blob/main/packages/core/AGENTS.md#cross-module-coupling)

Tenant-owned data is expected to carry tenant and organization scope, and handlers/helpers must apply that scope. This is application/ORM-level scoping; it is not evidence of PostgreSQL RLS. Flatpack should retain its stronger database-enforced RLS backstop rather than replacing it with query filters. [OpenMercato architecture security section](https://www.openmercato.com/architecture), [data and security rules](https://github.com/open-mercato/open-mercato/blob/main/AGENTS.md#data--security)

### Module-owned permissions

Modules publish stable feature IDs from `acl.ts`, and guarded pages/routes require features rather than mutable role names. A module's `setup.ts` declares which standard roles receive those features for new tenants; a synchronization command applies newly declared defaults to existing tenants. [Module ACL/setup rules](https://github.com/open-mercato/open-mercato/blob/main/.ai/docs/module-development.md#module-rules), [real module ACL](https://github.com/open-mercato/official-modules/blob/main/packages/carrier-inpost/src/modules/carrier_inpost/acl.ts), [real tenant setup](https://github.com/open-mercato/official-modules/blob/main/packages/carrier-inpost/src/modules/carrier_inpost/setup.ts)

This matches Flatpack's current direction: modules define immutable permission capabilities, while platform and organization administrators compose those capabilities into their separate custom roles.

### Package installation and dependency boundaries

Official modules are ordinary npm packages. The CLI installs the dependency, discovers and validates its module folder, adds an explicit entry to `src/modules.ts`, and regenerates the registries. Package mode leaves the implementation in the dependency; `--eject` copies an explicitly ejectable module into the application's source tree so the application owns it. Third-party packages outside the official scope require an explicit opt-in flag. [Official Modules repository](https://github.com/open-mercato/official-modules#how-it-works), [current install implementation](https://github.com/open-mercato/open-mercato/blob/main/packages/cli/src/lib/module-install.ts)

Published modules declare peer-dependency compatibility with host packages. OpenMercato's module rules discourage hard dependencies on optional peers; optional collaboration must use stable contracts or guarded resolution. [Official package example](https://github.com/open-mercato/official-modules/blob/main/packages/carrier-inpost/package.json), [optional dependency guidance](https://github.com/open-mercato/open-mercato/blob/main/.ai/docs/module-development.md#module-rules)

### Pluggable AI assistants

This part is genuinely modular. Any module can contribute `ai-agents.ts` and `ai-tools.ts`; generation aggregates them into registries. An agent definition includes a stable ID/module, prompt, execution mode, required features, exact tool allowlist, accepted media types, provider/model controls, loop/budget settings, and a mutation policy. Tools have input schemas, their own required features, and an explicit mutation classification. [AI agent contract](https://github.com/open-mercato/open-mercato/blob/main/packages/ai-assistant/src/modules/ai_assistant/lib/ai-agent-definition.ts), [AI package exports](https://github.com/open-mercato/open-mercato/blob/main/packages/ai-assistant/src/index.ts), [customers assistant example](https://github.com/open-mercato/open-mercato/blob/main/packages/core/src/modules/customers/ai-agents.ts)

Runtime policy checks require both agent-level and tool-level permissions, reject tools outside the agent allowlist, default mutation policy to read-only, and prevent tenant configuration from becoming more permissive than the code-declared policy. Write-capable tools flow through a pending-action approval mechanism rather than mutating immediately. [Agent policy gate](https://github.com/open-mercato/open-mercato/blob/main/packages/ai-assistant/src/modules/ai_assistant/lib/agent-policy.ts), [mutation preparation API](https://github.com/open-mercato/open-mercato/blob/main/packages/ai-assistant/src/index.ts#L123-L139)

Other modules or the application can add allowed tools/prompt material to an existing agent, replace an agent/tool, or disable it. The documented precedence is base contribution, file-based override, application module configuration, then programmatic override. [AI overrides](https://github.com/open-mercato/open-mercato/blob/main/packages/ai-assistant/src/modules/ai_assistant/lib/ai-overrides.ts)

## Maturity and lifecycle caveats

OpenMercato's current CLI implementation exposes `module add`, `module enable`, and eject flows. It does not currently expose the richer `remove`, `upgrade`, `outdated`, or `doctor` lifecycle described in its marketplace proposal. [Current CLI command implementation](https://github.com/open-mercato/open-mercato/blob/main/packages/cli/src/mercato.ts#L1357-L1411)

The lifecycle document proposes compatibility ranges, local state tracking, diagnostics, and conflict-aware upgrades, but its own status is Draft and it explicitly reserves module removal and package-signature verification for future work. It should be treated as design inspiration, not a shipped guarantee. [Lifecycle proposal](https://github.com/open-mercato/open-mercato/blob/main/.ai/specs/implemented/SPEC-061-2026-03-13-official-modules-lifecycle-management.md)

Likewise, disabling a module does not safely erase its data. Migrations are durable schema history; uninstalling code must not implicitly drop tables. This is the correct posture for Flatpack too: disable first, retain data by default, and make destructive purge a separate explicit operation with dependency checks and backup guidance.

## Recommended Flatpack architecture

### 1. Define one small, stable module kernel

Create `Flatpack.Modules.Abstractions` with a versioned descriptor rather than letting modules depend on `Api`, `Infrastructure`, or React internals:

```text
FlatpackModuleDescriptor
  Id, DisplayName, Version
  Requires, OptionalDependencies, CompatibilityRange
  Permissions, DataSchemas
  BackendContribution
  WebContribution
  AssistantContribution
```

The backend contribution should expose explicit composition hooks for services, endpoints, health checks, jobs, OpenAPI, and migrations. The host should consume a generated or hand-authored module catalog—never unrestricted reflection scanning.

### 2. Package a module as one versioned full-stack bundle

A distributable module should have:

- one NuGet package containing contracts plus backend implementation and migrations;
- one npm package containing React routes and UI contributions when the module has a web surface;
- one signed/checksummed Flatpack module manifest binding the backend and frontend package versions;
- a source/eject mode for teams that want permanent local ownership.

The Flatpack CLI should install or scaffold both halves and update explicit registries and lockfiles. The `.NET new` template remains the application generator; module installation is a separate lifecycle command.

### 3. Add stable React extension slots

Define typed, named contribution points in the owned React shell and shared components:

- routes and navigation groups;
- dashboard widgets and page actions;
- DataTable columns, filters, row actions, and bulk actions;
- form fields and detail sections;
- command-palette actions and notification renderers.

Contributions should be ordered deterministically, permission-gated, lazy-loaded, and validated at build time. A module may extend a host slot; it must not reach into another feature's private component tree.

### 4. Make permissions and tenant isolation part of the module contract

Every module declares stable permission definitions with human metadata and dependencies. Installing a module extends the backend-owned permission catalog; roles remain platform-owned or organization-owned exactly as they are today. Module endpoints and background workers must use the same permission handlers and organization context as core features.

Every organization-owned entity must still carry `OrganizationId`, be covered by RLS policy and application constraints, and run under transaction-local `app.organization_id` and `app.actor_id`. No plugin is allowed to opt out of these requirements.

### 5. Add a secure assistant extension contract

A module may contribute agents and tools, but the manifest must declare:

- stable agent/tool IDs and owning module;
- required platform or organization permissions;
- exact tool allowlists and accepted input/attachment types;
- read-only versus mutating classification;
- approval policy for every mutation;
- execution, time, token, and tool-call budgets;
- tenant/organization scope requirements;
- provider/model requirements without embedding secrets.

The runtime should reauthorize every tool call, pass organization/actor context server-side, record trace/audit metadata, and stage mutations for explicit confirmation. Tenant configuration may disable or further restrict an assistant, never widen the permissions or mutation policy declared by code.

### 6. Design lifecycle safety before calling it a marketplace

The CLI and module lockfile should support:

```text
flatpack module list
flatpack module add <package>
flatpack module enable|disable <id>
flatpack module doctor
flatpack module upgrade <id>
flatpack module eject <id>
flatpack module remove <id>        # keeps data by default
flatpack module purge-data <id>    # separate, destructive, explicit
```

Install/upgrade must validate package provenance, compatibility range, dependency graph, duplicate IDs/permissions/routes/slots, migration order, and frontend/backend version alignment before changing the application. Removing a depended-on module must fail. Upgrades of ejected modules should be assisted merges, never overwrites.

### 7. Preserve deep boundaries in the canonical solution

Start by modularizing the canonical source before publishing third-party packages:

1. Introduce the module abstractions and explicit catalog.
2. Convert the existing Project reference feature into the first complete module.
3. Move current optional email, storage, documents, and images features onto the same contract.
4. Add named React extension points and generated frontend registry.
5. Add dependency/permission/route/slot validation and disabled-module tests.
6. Add assistant/tool contracts and a read-only reference assistant before enabling mutations.
7. Add CLI lifecycle and packaging only after install, upgrade, disable, removal, migration, RLS, and template-generation tests are automated.

## Bottom line

Yes, Flatpack can be made comparably modular—and it can be safer in the areas that matter for a .NET multi-tenant template. The target should be **install-time composition with build-time validation**, not arbitrary runtime loading. OpenMercato supplies useful extension vocabulary and a strong assistant model; Flatpack should combine those ideas with Clean Architecture, explicit DI, EF Core migration ownership, OpenAPI generation, PostgreSQL RLS, and conservative package lifecycle rules.
