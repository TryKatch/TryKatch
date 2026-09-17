# Verification by change surface

Run from the application root containing `Trykatch.slnx` and `trykatch.modules.json`. In generated applications, the paths below are renamed to the application's namespace. Confirm the actual files before running them. Use the local CLI through `dotnet run --project tools/Trykatch.ModuleTool --`.

| Changed surface | Required evidence for that surface |
| --- | --- |
| Instructions/docs only | Skill frontmatter, resolvable references, accurate commands; template maintainers also test packed full-stack and backend-only output |
| Domain or application behavior | Build and affected module unit tests; regression/acceptance examples for the changed rule |
| Module composition, project references, public contracts | Module doctor, host and affected module architecture tests, caller compatibility; check enable/disable when affected |
| API, authorization, persistence, transactions | Relevant HTTP integration tests; real PostgreSQL tests for RLS/atomicity claims; rebuilt OpenAPI |
| Frontend or API consumed by React | Client generation, typecheck, affected tests, production build, and changed flow in a browser |
| Migration or data ownership | Forward-migration review plus real PostgreSQL migration/isolation tests under the runtime role |

## Backend commands

```bash
dotnet restore Trykatch.slnx
dotnet build Trykatch.slnx --no-restore
dotnet run --project tools/Trykatch.ModuleTool --no-build -- module doctor
dotnet test tests/Trykatch.ArchitectureTests --no-build
dotnet test tests/Trykatch.UnitTests --no-build
```

Choose the affected module's actual unit/architecture projects under `tests/Modules/<Module>/`. Do not restrict coverage to the original Projects example when another module changed. If a previous CLI creation already built and tested the same files, reuse that evidence.

The host architecture checks inspect Debug output. Build Debug before those checks; do not reuse a Release-only build with the default `--no-build` commands. Read `.github/workflows/ci.yml` for the installed application's full gate.

Integration tests live in `tests/Trykatch.IntegrationTests`. Select existing relevant tests with `--filter`, then add acceptance tests for new behavior not covered by the generic suite. Tests using containers require a working Docker daemon. Review results for skipped tests: a skipped PostgreSQL test does not verify isolation.

```bash
dotnet test tests/Trykatch.IntegrationTests --no-build --filter FullyQualifiedName~PostgresIsolationInspectionTests
```

For final verification of broad backend changes, run the integration project without the filter. Applying migrations to shared or deployed databases is not a prerequisite for local verification; use an isolated test database.

### Shipment blueprint HTTP example

After creating the shipped `ShipmentReceptions` workflow, use [ShipmentBlueprintAcceptanceTests.cs.fixture](../../blueprints/tests/ShipmentBlueprintAcceptanceTests.cs.fixture) as its HTTP acceptance starting point. Copy it into `tests/Trykatch.IntegrationTests/ShipmentBlueprintAcceptanceTests.cs` only if that test does not already exist. The fixture is namespace-renamed during application generation and uses real PostgreSQL plus the production API test host. It is not compiled while it has the `.fixture` suffix.

Adapt its assertions for any changed business rules and add boundary cases; the fixture cannot prove a custom guard it never exercises. Then compile and run it (do not use `--no-build` after adding the test):

```bash
dotnet test tests/Trykatch.IntegrationTests --filter FullyQualifiedName~ShipmentBlueprintAcceptanceTests
```

For a different module, author corresponding tests using the host's existing fixtures and that module's actual endpoints; do not run the shipment scenario unchanged as evidence for another feature. Keep focused tests under `tests/Modules/<Module>/` for domain behavior, and use the host integration project for HTTP/database behavior.

## Frontend and generated contracts

Only if `web/package.json` exists:

```bash
corepack pnpm --dir web install --frozen-lockfile
corepack pnpm --dir web generate
corepack pnpm --dir web generate:check
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
```

Build the backend first when endpoints changed. `generate:check` checks the assistant contract against the current OpenAPI; it does not prove that the OpenAPI or TypeScript client is current. Inspect the generated diff after the build/generation sequence. Never hand-edit a generated contract to make a check pass.

Backend-only applications put generated OpenAPI under `src/API/Trykatch.Api/obj/openapi` and do not have the web generation pipeline. Do not run `pnpm --dir web` or claim the shipped assistant JSON was refreshed there.

Use the app's existing browser tests or available browser tooling for the changed user flow. Start the test application only with the documented local prerequisites, and keep permission/error/conflict cases in the scenario when relevant. For responsive changes, use the widths in [frontend architecture](../frontend-architecture.md). Report browser checks that were not run explicitly.

## Report

Record passed, failed, and not-run checks with the relevant reason. Tie acceptance claims to the test layer that actually established them. Inspect the diff for unintended generated drift and temporary artifacts. When a check fails, fix within the authorized task and rerun the affected checks; do not suppress the failure or silently reduce the requirement.
