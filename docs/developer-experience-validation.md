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

Idempotency keys, jobs, notifications, upgrade assessments and global search remain future work. No full release qualification matrix or live-backend browser conflict test was run. Disposable test applications, package hives and PATH shims were moved to macOS Trash after validation and remain recoverable there; source and reports remain in the feature worktree.

## Conflict recovery continuation — 2026-09-16

The local `test/generated-conflict-recovery` branch extends the existing stack. Generated CRUD and blueprint packages now include component regression tests for stale-save blocking, explicit refresh, preservation of entered values, retry with the refreshed version, failed refresh, and cancellation while refreshing. Test fixtures derive their DTO fields from the module contract; blueprint fixtures also declare workflow metadata and mock their named API operations. Generated packages declare the test dependencies explicitly.

The new cancellation test first failed against the existing plain CRUD editor: a late refresh response reopened a cancelled dialog with reset form fields. The response handler now applies the refreshed record only when an editor is still open for that record. The same test passes after regeneration; workflow editors already had the corresponding guard.

Follow-up evidence:

- `dotnet test tests/Trykatch.UnitTests`: 280 passed, zero failed or skipped. This includes rendering fixtures for all supported field kinds, optional booleans, one-character strings, and blueprint mocks. These generator assertions are not a claim of browser coverage for every possible field contract.
- `scripts/test-business-blueprint.sh web`, with isolated Corepack shims and pinned pnpm 10.17.1: passed. Actual packed template/CLI output generates ShipmentReceptions and Invoicing; backend/module checks, frontend type checks, regenerated client, component tests, production builds, and module doctor pass.
- The harness's two selected real-PostgreSQL integration tests passed with no skips. Their assertions cover tenant default deny across generated relations, SQL pagination, required/stale versions, competing CRUD writes, workflow races, permissions, and transactional evidence.
- Chromium against the packaged production frontend with mocked API responses: stale PUT returns 409, Save is disabled, explicit GET refresh preserves entered text, and the retry sends the refreshed version and preserved text before returning 200 and closing the editor. These are frontend/HTTP-client checks, not a live-backend browser test.
- Chromium at 390 × 844: invoice editor visually inspected; document width equals viewport width. A held refresh response released after Cancel does not reopen the editor. With read-only access, New/Edit/Archive controls are absent and the row menu contains only View. Browser console errors were the two deliberately mocked 409 resource responses, not JavaScript exceptions.

The browser and preview process were stopped. The four disposable acceptance workspaces and Corepack shim directories from this continuation were moved to macOS Trash and remain recoverable. The earlier blog and research drafts are preserved. No shared database was migrated; no branches were pushed, merged, or released. A full release matrix and live-backend browser conflict test remain unverified.
