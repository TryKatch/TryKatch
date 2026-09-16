# Developer experience verification — 2026-09-16

## Delivered

Three stacked feature branches extend `feat/development-skills`:

1. `feat/generated-module-reliability`: additive SQL-backed page contracts, generated server-side search/order/page controls, CRUD expected-version and EF concurrency protection, explicit conflict refresh.
2. `feat/setup-diagnostics`: safe application setup, prerequisite diagnostics, configuration/live-health status.
3. `feat/typed-module-extensions`: typed table columns/actions, permission filtering, deterministic composition, checked point identity, stable-ID overrides and generated table hooks.

The primary checkout remains on `develop`. No branches were published, merged or released.

## Checks

- All 270 host unit tests pass, including scaffolder contracts, rollback, prerequisite checks, backend-only setup, cancellation, health responses, reserved field names and older-SDK rejection.
- Frontend workspace type check, tests and production build pass. The SDK tests cover typed composition, invariant DTOs, permission filtering, point identity, erased-contribution rebinding, duplicates and overrides. Host UI tests render restricted columns/actions and exercise a row action through the provider.
- `scripts/test-business-blueprint.sh web` passes using the workspace-pinned pnpm 10.17.1 through a temporary PATH shim. It packs and installs the template and CLI, generates ShipmentReceptions and plain Invoicing, compiles both surfaces, generates the API client, and runs generated unit/architecture/frontend tests and builds.
- Real PostgreSQL acceptance verifies workflow rules, paged string search, ordering, empty pages, page/query bounds, literal wildcard search, required versions, competing CRUD writes (one success/one conflict), lifecycle version rotation, workflow races, permissions and transactional evidence. All generated organization relations pass the real runtime-role RLS default-deny test.
- A dotted-name backend-only app (`Backend.Diagnostics`) successfully generates a numeric-only Metrics module and passes backend compilation, unit/architecture tests and module doctor. Its `setup` checks/restores .NET only.
- Actual `setup` and `doctor` pass against the generated full-stack app with pinned pnpm. `status` without a URL explicitly reports unknown runtime health. HTTP status behavior is also tested through a fake HTTP handler; no live Aspire deployment is claimed.
- Release-version and CI change-classification checks pass.

## Findings and limits

The real doctor identified PATH pnpm 11.19.0 versus Corepack's pinned 10.17.1. The global installation was left unchanged. A workspace previously installed with the mismatched version correctly refuses a noninteractive destructive reinstall; setup does not force-purge dependencies. A freshly generated workspace using the pinned executable successfully runs setup.

Missing expected versions fail JSON request binding with 400; stale versions return structured 409. Search covers generated string fields and creation-date ordering supports newest/oldest. Pagination is offset-based, not a snapshot or keyset cursor; concurrent inserts can shift pages. The legacy array endpoint and current archive loader remain unpaged for compatibility.

Typed extensions currently cover columns and row actions, not form/filter contracts. Contribution permissions are presentation filtering, not server authorization. Updating the CLI does not retrofit an existing app, module schema or SDK; web generation requires the coordinated `typed-tables-v1` host capability. Do not add the marker without upgrading the implementation.

Idempotency keys, jobs, notifications, upgrade assessments and global search remain future work. No full release qualification matrix or browser end-to-end conflict test was run. Disposable test applications, package hives and PATH shims were moved to macOS Trash after validation and remain recoverable there; source and reports remain in the feature worktree.
