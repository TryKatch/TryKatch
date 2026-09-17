# From first run to first feature

[Français](first-feature.fr.md) · [Developer onboarding](developer-onboarding.md)

Build a small Equipment catalog to learn the complete module path. This is an educational organization-owned CRUD example, not a rental-management product: availability, bookings, billing and non-negative pricing need additional rules and tests. Use a fresh generated application and disposable records, not production. No AI provider key is needed.

## 1. Check before starting

From the root containing `Trykatch.slnx` and `trykatch.modules.json`, use the source-local CLI so contracts match this checkout:

```bash
dotnet run --project tools/Trykatch.ModuleTool -- doctor
dotnet run --project tools/Trykatch.ModuleTool -- setup
dotnet run --project tools/Trykatch.ModuleTool -- status
```

If the CLI cannot compile, follow the [README prerequisites](../README.md) first. `doctor` checks SDK, Docker, certificate presence and module graph. With web installed, it also checks Node, Corepack and **both** Corepack-selected and PATH-selected pnpm against `web/package.json`. A different global pnpm can block module creation even if a manual install worked; resolve the mismatch rather than bypassing the check. Stay in this application, not a parent whose `packageManager` selects another tool. See the [CLI reference](../tools/Trykatch.ModuleTool/README.md).

`setup` restores locked dependencies; it does not start services, trust certificates, create secrets or migrate a database. Backend-only output skips frontend steps. Certificate presence does not establish trust; complete the development certificate configuration if needed, without disabling TLS verification.

Checkpoint: `doctor` and `setup` succeed. Without an API URL, `status` still says `Runtime health: unknown`: configuration or compilation is not proof of a running application.

## 2. Prove startup

```bash
dotnet run --project tools/Trykatch.ModuleTool -- start
```

Keep the terminal open. AppHost coordinates PostgreSQL, the one-shot migrator, API and installed web resources. Take the resource URLs from Aspire; do not assume ports. In another terminal, replace the placeholder with the actual API origin (scheme, host, port only):

```bash
dotnet run --project tools/Trykatch.ModuleTool -- status --api-url <API-origin-from-Aspire>
```

Checkpoint: `/health/live` and `/health/ready` both return HTTP 200. Otherwise inspect the failing resource's Aspire logs; a Vite page or dashboard alone does not prove API readiness. Open the web resource, sign in with the development-only workspace Owner described in the README and confirm Projects loads. Backend-only: follow sections 1–2 of the [HTTP walkthrough](backend-only-http.md) for demo credentials, cookie login, refreshed antiforgery and workspace selection, then log out before stopping AppHost. There is no React page or generated API client.

Stop AppHost with Ctrl+C before generation to avoid stale composition and concurrent shared build outputs. Do not terminate unrelated applications.

## 3. Generate Equipment

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module facts
dotnet run --project tools/Trykatch.ModuleTool -- module create Equipment \
  --entity EquipmentItem --resource equipment_items --ownership organization \
  --fields "name:string:required:max(120),dailyRate:decimal:required" \
  --with-web
```

Omit `--with-web` without `web/package.json`. If Equipment or its resource already exists, inspect it instead of rerunning creation over customized source. Organization/actor IDs come from authenticated context, never editable fields.

Checkpoint: numbered steps report `OK`; the final summary lists paths, endpoints and permissions. Generation registers/enables, restores, builds and runs generated module unit/architecture tests. With web it also generates the OpenAPI client, checks types, tests and builds the frontend. This does not prove live CRUD or every business rule. On `FAIL`, retain diagnostics and confirm the announced rollback before resolving the cause and retrying. Do not bypass checks or mutate the workspace concurrently.

## 4. Read the output

| Output | Meaning and next action |
| --- | --- |
| `src/Modules/Equipment/` | Your editable source: Domain owns invariants, Application authorized use cases, Presentation HTTP binding, Infrastructure persistence/composition, IntegrationEvents public contracts. |
| Endpoints | Secured operations. Inspect exact routes/operation IDs in the API `/docs` after restarting. |
| Permissions | Installed definitions, not grants to everyone. Inspect manifest/provider and verify read versus manage access on the server. |
| Web route | `/equipment_items`; module Web owns forms, navigation and English/French messages. |
| Registries, lockfiles, API client | Machine-owned artifacts. Change source/manifests then regenerate; never hand-edit them. |

Read the catalog, module manifest, migration and local tests. `name` is required with maximum length 120; `dailyRate` uses `numeric(18,2)` and an invariant JSON **string**, not a JavaScript number. Forced RLS and shared transaction context scope data to the authenticated organization. Read [isolation](module-data-isolation.md) before changing persistence. Use IntegrationEvents/outbox for cross-module communication, not another module's private repositories.

The generator supplies EN/FR interface catalogs, not automatic business translations. Review editable `src/Modules/Equipment/Web/src/messages.ts`: for example, translate the French `fieldDailyRate` from its humanized `Daily rate` default to `Tarif journalier`, and review Equipment/entity wording before the language checkpoint. Skip this without web. Rerun affected frontend checks after such edits.

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module facts
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
```

Checkpoint: Equipment is enabled and the graph is healthy. Permission declarations do not establish a test account's grants.

## 5. Exercise the live feature

Restart with `start` and repeat `status --api-url`. The migrator applies the new forward-only migration before API startup. As workspace Owner, open `/equipment_items`, create `Training excavator` with `125.50`, refresh, edit its rate and refresh again. Verify persisted values. Exercise required-name, length and decimal validation. Archive the disposable record and restore it through Archive. Switch English/French and check page, form and navigation wording.

Backend-only: repeat login and workspace selection, then run the Equipment create/read/update/archive/restore requests in the [HTTP walkthrough](backend-only-http.md). It uses curl rather than a nonexistent generated client; do not invent a password-grant/token endpoint. Separately provision test memberships with the generated read/manage permissions. Verify read-only cannot mutate, denied accounts cannot access, and a second organization cannot read/change the first organization's record ID. UI hiding is not authorization; a database-owner account is not RLS evidence. Verify denied writes leave business state/audit/outbox consistent. Clean up only disposable records through supported lifecycle operations.

Checkpoint: record passed, failed and not-run checks. Generated tests are not a substitute for live HTTP/browser exercises.

## 6. Verify and extend

Use the [verification matrix](development/verification.md). Reuse successful generation checks until subsequent edits invalidate them. After custom code, rebuild and rerun affected module and host architecture tests. Real PostgreSQL checks must run, not skip:

```bash
dotnet test tests/Trykatch.IntegrationTests \
  --filter FullyQualifiedName~EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole
```

That checks declared relations under the real runtime role, not Equipment's HTTP workflow. Add explicit API/integration tests for your business rules. With web installed, rebuild the API and use `generate` to regenerate its OpenAPI client and assistant contract. Then check assistant-contract freshness:

```bash
corepack pnpm --dir web generate:check
```

`generate:check` checks the assistant contract only, not the TypeScript client's freshness. Review regenerated client files against your task's baseline, including new/untracked files, then typecheck/test/build per the matrix. CI compares tracked OpenAPI/client/contract output after regeneration; an empty `git diff` alone does not verify freshly generated untracked files. Never patch the client directly.

Ready to extend means startup is proven, source boundaries understood, live CRUD/validation/permission/language checks recorded and selected automated checks pass. Then ask a developer unfamiliar with Trykatch to follow this without coaching; automation does not prove usability.

Next question for your coding assistant:

```text
Read AGENTS.md and docs/first-feature.md. Inspect Equipment and explain its
request path with source references. Propose where a non-negative daily-rate
rule belongs and tests to prove it. Do not change files or start services until
I approve implementation.
```
