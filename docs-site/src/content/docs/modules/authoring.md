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

`Invoicing` and `Invoice` must be portable PascalCase .NET identifiers: Trykatch also rejects host names and Windows device names such as `CON`, `AUX`, `COM1`, and `LPT1`. `invoices` must be an explicit lower-case snake_case PostgreSQL identifier, must not be a PostgreSQL keyword, and must not duplicate an `app` schema relation declared by another registered module. Version 1 deliberately requires `--ownership organization`; it never guesses the security boundary. These checks run before staging or modifying any workspace file.

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
| `decimal` | `decimal` with `numeric(18,2)` persistence | invariant string transport and exact decimal input |
| `int` | `int` | whole-number input |
| `long` | `long` | invariant string transport and exact 64-bit integer input |
| `bool` | `bool` | checkbox or optional selector |
| `date` | `DateOnly` | date input |
| `datetime` | UTC-normalized `DateTimeOffset` | local date-time input converted to an ISO UTC instant |
| `guid` | `Guid` | identifier input |
| `enum(Draft,Sent,Paid)` | strongly typed `InvoiceStatus` | translated selector |

If `--fields` is omitted, the compatible starter contract remains `name:string:required:max(200),description:string:optional:max(2000)`. Platform-managed fields such as `Id`, `OrganizationId`, audit timestamps and deletion metadata cannot be declared or exposed as writable fields.

Field identifiers are limited to 63 ASCII characters so PostgreSQL cannot silently truncate a generated column name. Decimal values accept at most 16 integer digits and 2 fractional digits, matching `numeric(18,2)` exactly. Decimal and 64-bit integer values cross JSON as invariant strings so JavaScript cannot round them. Generated date-time values are normalized to UTC before persistence and converted between the browser's local editor and ISO UTC transport.

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

The command also adds the projects to the solution in deterministic folder/project order, registers the Infrastructure entrypoint with the API and migrator, adds and enables the catalog entry, regenerates all registries, restores dependencies, builds the backend, runs the generated tests and module doctor, and—when requested—generates the OpenAPI client and runs frontend type checking, tests, and the production build. Success output lists every generated endpoint, both permissions, and the exact start command.

The operation is atomic. Rendering happens in a private staging directory, where the complete rendered manifest and projected module catalog are validated before any module file is committed. If solution editing, registration, restore, build, testing, client generation, or doctor validation fails—or you interrupt the command with Ctrl+C—Trykatch terminates the active child command, restores the catalog, solution, project files, registries, OpenAPI/client output, and lockfiles, then removes the new module. Repeating the same command reports that the module exists and makes no changes; v1 has no overwrite option.

The starter application enables both reference modules for different reasons: Projects demonstrates ordinary organization-owned CRUD, while Documents demonstrates organization-isolated file upload and download through private S3-compatible storage. Documents is not a second CRUD clone; its metadata is protected by EF filtering and PostgreSQL RLS while file bytes stay outside PostgreSQL.

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

Reads require `invoicing.read`; mutations require `invoicing.manage`, permission checks are repeated in the application use cases, mutation endpoints require antiforgery protection, and writes record audit and outbox evidence in the host transaction. Minimal API handlers use typed result unions and stable OpenAPI operation names (`Invoicing_List` through `Invoicing_RequestDeletion`). The outbox publishes distinct immutable contracts—`InvoiceCreated`, `InvoiceUpdated`, `InvoiceArchived`, `InvoiceRestored`, and `InvoiceDeletionRequested`—instead of a free-form operation string.

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

## Generate a business workflow from a blueprint

The application must declare `business-blueprints-v1` in its catalog’s `hostCapabilities`. Updating the CLI or installed template does not modify an existing application. Do not add this marker to bypass the check: generate from the coordinated release, or migrate and test the HTTP binding-error handler, version-aware Archive SDK/host integration and EN/FR navigation labels before declaring this capability.

Use a JSON blueprint when a module needs business decisions, not just editable fields. The shipped example is `blueprints/shipment-reception.json`. Run these commands **from the generated application root**, not from `web/`:

```bash
cd /path/to/Horizon
trykatch module validate --blueprint blueprints/shipment-reception.json
trykatch module create ShipmentReceptions --blueprint blueprints/shipment-reception.json --with-web
trykatch start
```

Validation is read-only. Creation adds the module, builds and tests it, and registers its backend and React surface. Omit `--with-web` for backend-only generation. Do not combine `--blueprint` with `--fields`, `--entity`, `--resource`, `--ownership` or `--description`: the blueprint owns those decisions.

In the Aspire dashboard, open the **web** resource and navigate to `/shipment_receptions`. Open the **api** resource at `/docs` to inspect the generated operations in Scalar. Start Docker before `trykatch start`; the application needs PostgreSQL even when optional observability is disabled.

### What the shipment example enforces

- A reception begins in **Draft**. Its reference is required and its received/dispatched weights must be positive.
- Draft records may be edited and **submitted** for review.
- A submitted record may be **accepted** only when its documents are verified, or **rejected** with a 10–500 character reason.
- Accepted/rejected records cannot be reopened by changing a status field. Archive/restore changes visibility, not the business decision.
- Submission uses `shipment-receptions.submit`; decisions use `shipment-receptions.review`. Review defaults to administrators, not ordinary members.
- Every update, workflow action and recovery mutation requires the record's `expectedVersion`. A stale version returns HTTP 409; the React form preserves input and lets the user explicitly reload the latest record before retrying.

The server enforces these rules. Hiding an unavailable action in React is only a convenience, never an authorization boundary. New business state cannot be assigned through the CRUD request.

### Blueprint schema v1

The root declares `schemaVersion: 1`, `module`, `entity`, `resource`, explicit `ownership: "organization"`, English/French singular/plural `labels`, `fields`, and `workflow`.

Fields reuse the supported CRUD types. A field adds translated `label`, `required`, string `minimumLength`/`maximumLength`, or numeric `minimum`/`maximum` and `exclusiveMinimum`. Numeric bounds must be representable by the field type; decimals retain the exact `numeric(18,2)` contract.

Workflow declares named, translated `states`, one `initialState`, `editableStates`, and explicit `actions`. Each action has `name`, `from`, `to`, `permission`, `label`, and optional `inputs`, `guards`, `assignments`. All declared states must be reachable.

Guards are data, never executable expressions. Supported operators are `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `notEmpty`, `all` and `any`. A leaf references one `field` or `input`, with a type-correct literal `value` or a same-type `compareToField`. Groups contain `rules`. Every guard carries a stable `code` and English/French `message`. For example, an optional business policy can compare the received weight with the dispatched weight:

```json
{
  "op": "lte",
  "field": "receivedWeight",
  "compareToField": "dispatchedWeight",
  "code": "received_weight_exceeds_dispatch",
  "message": {
    "en": "Received weight cannot exceed dispatched weight.",
    "fr": "Le poids reçu ne peut pas dépasser le poids expédié."
  }
}
```

**Deliberate v1 limits:** actions use `submit` or `review` permission categories; action inputs are bounded strings; assignments may copy a declared input to `decisionReason` only. Arbitrary scripts, relationships, cross-module writes, automatic regeneration over customized source, and durable request-idempotency keys are not supported. Add bespoke business behavior in the generated domain/application code with tests and a forward-only migration where needed. A generator does not certify an application as enterprise-ready.

The normalized blueprint is retained as `src/Modules/ShipmentReceptions/module.blueprint.json`. Its module projects keep the same dependency boundaries, central EF organization filter, forced PostgreSQL RLS, permission enforcement, antiforgery, and transactional audit/outbox as other generated modules. Workflow changes use private domain state and EF optimistic concurrency. The generated React client calls the named OpenAPI operations.

### Verify the business behavior

```bash
# From the generated application root:
dotnet test tests/Modules/ShipmentReceptions/Horizon.Modules.ShipmentReceptions.UnitTests
dotnet test tests/Modules/ShipmentReceptions/Horizon.Modules.ShipmentReceptions.ArchitectureTests
trykatch module doctor
```

Replace `Horizon` with your application's namespace. Generated tests cover the workflow's lifecycle and version invariants; write additional tests for your own domain rules. The Trykatch repository also runs an independent shipment HTTP acceptance test and real PostgreSQL cross-organization tests against newly generated applications in CI.
