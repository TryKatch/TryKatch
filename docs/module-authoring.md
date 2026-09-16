# Module authoring direction

The generated template's operational module contract and reference layout live in [`templates/trykatch/docs/modules.md`](../templates/trykatch/docs/modules.md). The canonical solution must prove a module seam before Trykatch publishes a module marketplace or promises hot installation.

The delivery order is:

1. Explicit backend and React module catalogs with dependency validation.
2. Projects as the first tier-spanning reference module. **Implemented.**
3. A package-shaped `Federation` module with a manifest, secured API contribution, owned permissions, React routes, navigation, and module migrations. **Implemented.**
4. Module-owned permissions, RLS, audit/outbox behavior, routes, and navigation. **Implemented for Projects and Documents; generated organization CRUD modules receive the same mandatory contract. A validated `--fields` contract now drives the domain, API, PostgreSQL and optional React CRUD shape without exposing platform-owned security fields.**
5. Named web extension slots beyond routes and navigation. **Implemented for UI slots; typed table/form/command contracts remain.**
6. A CLI-managed NuGet + npm package pair with create, add, disable, doctor, upgrade, eject, remove, and separate purge-data workflows. **The atomic backend/full-stack source generator, authoritative catalog, list, doctor, deterministic generation, package acquisition/upgrade/eject/unregister, and dependency-safe enable/disable are implemented; permanent purge remains deliberately separate.**
7. A provider-neutral assistant contract generated from explicitly allowlisted module operations. **Implemented for strict read-only tools; a runtime provider adapter, permission-filtered discovery, approval UI, and mutation execution remain.**

CI enforces catalog validity, backend/web registry parity, deterministic generated modules, generated-module build/tests, and organization isolation against PostgreSQL. External third-party module installation remains an architectural capability under development rather than a promise of safe hot-loading of arbitrary code.

`module create` reports named, numbered progress steps with elapsed time. Interactive terminals animate a spinner during dependency restore, builds and tests; redirected output and `TERM=dumb` use plain lines. Optional frontend phases are reported only with `--with-web`. Failures mark the current step, announce rollback and retain command diagnostics; Ctrl+C uses the same rollback path. Progress rendering never changes the generator's security or verification requirements.

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
