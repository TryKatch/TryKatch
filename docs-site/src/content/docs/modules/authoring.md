---
title: Author a module
description: Add a capability without weakening the security kernel or creating hidden runtime coupling.
---

A Trykatch module is a full-stack capability package with an explicit contract. Projects demonstrates a built-in capability, Federation demonstrates an optional platform adapter, and Documents proves an independently packaged organization-data module across .NET, React, migrations, permissions, audit, and lifecycle behavior.

## What a module can contribute

- API services and controllers;
- EF Core model configuration and migrations;
- stable permissions and safe role defaults;
- React routes, navigation, and named UI extensions;
- outbox events, subscribers, and workers;
- explicitly allowlisted assistant tools.

## What remains centralized

Identity, organization resolution, RLS, antiforgery, permission enforcement, audit integrity, and module validation are not extension points.

## Generate a backend module

Run the generator from the root of an application already created with Trykatch—the directory containing the solution file, `src/`, `tests/`, and `trykatch.modules.json`:

```bash
cd /path/to/Horizon
trykatch module create Invoicing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --fields "number:string:required:max(40),total:decimal:required,dueDate:date:required,status:enum(Draft,Sent,Paid):required,notes:string:optional:max(2000)"
```

`Invoicing` and `Invoice` must be PascalCase .NET identifiers. `invoices` must be an explicit lower-case snake_case PostgreSQL identifier, must not be a PostgreSQL keyword, and must not duplicate an `app` schema relation declared by another registered module. Version 1 deliberately requires `--ownership organization`; it never guesses the security boundary. These checks run before staging or modifying any workspace file.

## Generate a full-stack module

Add `--with-web` to include a React Query list/create/edit surface, navigation, Archive integration, and module-owned English/French messages:

```bash
trykatch module create Invoicing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --fields "number:string:required:max(40),total:decimal:required,dueDate:date:required,status:enum(Draft,Sent,Paid):required,notes:string:optional:max(2000)" \
  --description "Organization invoice management." \
  --with-web
```

Use `trykatch module create --help` for the complete command contract.

## Describe the business fields once

`--fields` is the authoritative business shape for the generated CRUD slice. The generator applies it consistently to the domain entity, create/update contract, DTO, validation, EF Core configuration, PostgreSQL migration, OpenAPI document and—when `--with-web` is present—the React table, form, details view and English/French message catalogs.

Each definition uses `lowerCamelCase:type`, followed by optional modifiers. Fields are required by default; write `optional` when `null` is a valid business value. Strings accept `max(length)` from 1 through 10,000. Up to 24 fields are supported.

| Contract type | Generated .NET type | Generated React control |
| --- | --- | --- |
| `string` | `string` | text input or textarea |
| `decimal` | `decimal` with `numeric(18,2)` persistence | decimal number input |
| `int` / `long` | `int` / `long` | whole-number input |
| `bool` | `bool` | checkbox or optional selector |
| `date` | `DateOnly` | date input |
| `datetime` | `DateTimeOffset` | date-time input |
| `guid` | `Guid` | identifier input |
| `enum(Draft,Sent,Paid)` | strongly typed `InvoiceStatus` | translated selector |

If `--fields` is omitted, the compatible starter contract remains `name:string:required:max(200),description:string:optional:max(2000)`. Platform-managed fields such as `Id`, `OrganizationId`, audit timestamps and deletion metadata cannot be declared or exposed as writable fields.

## What the command creates

```text
src/Modules/Invoicing/
├── Horizon.Modules.Invoicing.Domain/
├── Horizon.Modules.Invoicing.Application/
├── Horizon.Modules.Invoicing.IntegrationEvents/
├── Horizon.Modules.Invoicing.Presentation/
├── Horizon.Modules.Invoicing.Infrastructure/
├── Web/                         # only with --with-web
├── trykatch.module.json
└── README.md
tests/Modules/Invoicing/
├── Horizon.Modules.Invoicing.UnitTests/
└── Horizon.Modules.Invoicing.ArchitectureTests/
```

The command also adds the projects to the solution, registers the Infrastructure entrypoint with the API and migrator, adds and enables the catalog entry, regenerates all registries, restores dependencies, builds the backend, runs the generated tests and module doctor, and—when requested—generates the OpenAPI client and runs frontend type checking, tests, and the production build.

The operation is atomic. Rendering happens in a private staging directory. If registration, restore, build, testing, client generation, or validation fails, Trykatch restores the catalog, solution, project files, registries, OpenAPI/client output, and lockfiles, then removes the new module. Repeating the same command reports that the module exists and makes no changes; v1 has no overwrite option.

## Generated security contract

The generated entity implements `IOrganizationOwned`. The host applies the named organization query filter and validates the `OrganizationId` shape and tenant-first index. Its PostgreSQL migration creates `app.invoices`, enables and forces RLS, and defines `invoices_organization_isolation` with both `USING` and `WITH CHECK`. The API exposes:

```text
GET    /api/v1/invoices/
GET    /api/v1/invoices/{id}
POST   /api/v1/invoices/
PUT    /api/v1/invoices/{id}
POST   /api/v1/invoices/{id}/archive
POST   /api/v1/invoices/{id}/restore
DELETE /api/v1/invoices/{id}
```

Reads require `invoicing.read`; mutations require `invoicing.manage`, permission checks are repeated in the application use cases, mutation endpoints require antiforgery protection, and writes record audit and outbox evidence in the host transaction.

## Start and verify the result

From the application root:

```bash
trykatch start
```

Open the Aspire dashboard, select the **api** resource, then use its HTTPS URL. `/docs` opens Scalar and `/openapi/v1.json` exposes the generated contract in Development. With `--with-web`, open `/invoices` on the React resource. You can re-run the deterministic checks directly with:

```bash
dotnet build Horizon.slnx
dotnet test tests/Modules/Invoicing/Horizon.Modules.Invoicing.UnitTests
dotnet test tests/Modules/Invoicing/Horizon.Modules.Invoicing.ArchitectureTests
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
trykatch module doctor
```

## Extend the generated entity safely

Add domain behavior to the entity instead of public setters. The generated fields are a starting contract; after the module has shipped, evolve it through explicit request/DTO changes and a new immutable forward-only module migration instead of rerunning the generator over existing source. Keep all organization access through `IOrganizationModuleData`; never inject a host DbContext or accept an organization ID from a request. Preserve stable endpoint names, permissions, event contracts, table name, and RLS policy unless you are deliberately versioning that public contract.

Persistent modules must also satisfy the [module data-isolation contract](/architecture/module-data-isolation/). Modules cannot opt out of organization scoping or receive direct access to host database contexts.

```bash
trykatch module list --root ./Horizon
trykatch module doctor --root ./Horizon
```

:::caution
The generator creates source modules inside an existing application. It does not turn untrusted packages into arbitrary runtime plugins; separately distributed packages still pass the signed package lifecycle and the same build-time validation boundary.
:::
