# Trykatch CLI

The Trykatch CLI validates and composes the backend and React surfaces of a generated Trykatch application from one authoritative `trykatch.modules.json` catalog.

```bash
dotnet tool install --global Trykatch.Cli --prerelease
trykatch help
trykatch template install
trykatch new Horizon
trykatch template help
trykatch start
trykatch module help
trykatch module create Invoicing --entity Invoice --resource invoices --ownership organization --fields "number:string:required:max(40),total:decimal:required,status:enum(Draft,Paid)" --with-web
trykatch module doctor --root /path/to/application
trykatch module register src/My.Module/module-manifest.json
trykatch module install ./downloaded/module-manifest.json --sha256 <published-digest>
trykatch module upgrade ./downloaded/module-manifest.json --sha256 <published-digest>
trykatch module disable <id>
trykatch module unregister <id>
trykatch module eject <id> --source-bundle ./reviewed-source --sha256 <published-digest>
trykatch template uninstall
```

`template install` invokes the official .NET template engine with an argument-safe process boundary. It shows a spinner in interactive terminals, emits deterministic progress in redirected output and CI, preserves the template engine's failure details, and defaults to the template version matching the installed CLI. Use `--version <version>` to select another release and `--force` to repair an existing installation.

`new <name>` creates the complete application and authorizes the packaged Git
initialization post-action. Standalone output starts on `main`; output already
inside a Git worktree remains part of its parent repository.

`start` discovers the generated Aspire AppHost from the current directory or `--root`, then runs its HTTPS launch profile with inherited terminal output. `template uninstall` removes `Trykatch.Templates` through the official .NET template engine; remove the global CLI separately with `dotnet tool uninstall --global Trykatch.Cli`.

`module create` scaffolds an organization-owned CRUD module inside an existing
Trykatch application. Use `--fields` to define the business contract once across
the domain, API, database and generated React CRUD surface. Supported types are
`string`, `decimal`, `int`, `long`, `bool`, `date`, `datetime`, `guid`, and
`enum(...)`; omit it for the compatible Name/Description starter. Add `--with-web`
for the React surface. The command stages
the source privately, validates the rendered manifest and projected catalog,
registers and enables the module, restores both package graphs, builds and tests
the result, and rolls every changed file back if any phase fails. It prints every
endpoint, the generated permissions, and the start command. Generated writes emit
distinct created, updated, archived, restored, and deletion-requested integration
event contracts. Run it from the generated application root or pass `--root`.

`trykatch.modules.lock.json` is machine-owned and records the manifest digest mode,
digest, version, enablement state, distribution kind, package pairing, and license
for every module. Installed packages use their exact SHA-256. Workspace manifests
use a template-name-normalized SHA-256 so a newly generated application keeps valid
provenance after `dotnet new -n` replaces its root namespace. Package install and
upgrade require an exact SHA-256 obtained through a separate trusted channel. They
pin the NuGet and npm identities declared by the manifest,
restore both dependency graphs with npm lifecycle scripts disabled, and roll the
catalog, registries, manifests, project files, and lockfiles back if validation or
restore fails.

New packages install disabled. Enablement is a separate reviewed action. Unregister
requires prior disablement, refuses installed dependents, and retains database data
and migration history. Ejection never overwrites source: it accepts only a matching,
checksum-verified workspace source bundle and switches both hosts to local project
and workspace references.

Use `module list`, `module generate`, `module enable <id>`, and `module disable <id>` to inspect and change the installed module graph. Enable and disable operations use atomic file replacement with rollback on failure and never remove module data. Destructive `purge-data` is intentionally not provided; modules must publish a separate reviewed retention runbook for permanent removal.

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
