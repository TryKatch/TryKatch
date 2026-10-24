# TryKatch Feature Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The project's Sol-lead/Luna-worker routing, single review workflow and targeted-test instructions take precedence over generic workflow defaults.

**Goal:** Add the missing FullStackHero application capabilities through TryKatch's existing security kernel and explicit full-stack module architecture.

**Architecture:** Extend the host only for shared execution/security/operational contracts; implement business behavior in six optional five-layer modules. Reuse Documents, cookie/BFF authentication, PostgreSQL RLS, transactional audit/outbox, generated API clients and the existing React shell. Changes are additive and retain existing migration/event identities.

**Tech Stack:** .NET 10, PostgreSQL/EF Core, React/TanStack, TypeScript, Aspire, OpenTelemetry, MSTest/Testcontainers, Vitest/Storybook/Playwright; new adapters require dependency review before installation.

**Spec:** [Functional parity design](../specs/2026-10-23-feature-parity-design.md).

**Status:** Phase 0 and Phase 1 E1–E4 are implemented and reviewed; E4 merged in PR #131 after its selected CI gates passed. E5 merged in PR #132 after exact-head CI and dependency review passed. Its automatic production API restart/two-replica checks and integrated review are complete; the Phase 1 installed-event/operation exit remains open. Later phases remain planned. The [E1](../../../templates/trykatch/docs/specs/evidence/parity-execution-e1.md), [E2](../../../templates/trykatch/docs/specs/evidence/parity-execution-e2.md), [E3](../../../templates/trykatch/docs/specs/evidence/parity-execution-e3.md) and [E4 evidence](../../../templates/trykatch/docs/specs/evidence/parity-execution-e4.md) record the actual qualification and remaining release limitations. E4 patches the earlier .NET advisory baseline; its dependency and qualification follow-ups passed before merge. See the [frozen contract ledger](../../../templates/trykatch/docs/specs/feature-parity-contracts.md), [acceptance matrix](../../../templates/trykatch/docs/specs/feature-parity-acceptance.md) and [Phase 0 evidence](../../../templates/trykatch/docs/specs/evidence/feature-parity-phase-zero.md). Original planning baseline: `origin/develop`, `d3e8dc40b6c5d4d20a173ec2f884353f4f25a7d9`, inspected 2026-10-23. Source inspection alone does not establish runtime readiness.

## Global Constraints

- Preserve .NET SDK `10.0.301` with `latestPatch` in the `10.0.3xx` feature band, Node.js `24+`, and pnpm `10.17.1`; no unrelated dependency upgrades.
- Preserve the non-replaceable security kernel, five module layers, IntegrationEvents-only cross-module references, explicit catalog, cookie/BFF + antiforgery and forced PostgreSQL RLS.
- Preserve atomic business/audit/outbox writes, immutable forward migrations, decimal/Int64 string transports, UTC timestamps, stable operation IDs and versioned event contracts.
- Optional modules are opt-in in generated products. Disabling/unregistering a module retains data and history.
- Reuse Storybook, shared UI/floating controls, English/French messages, generated clients and machine-owned registries.
- Platform authority does not imply organization membership. Async work, cache, realtime, exports and search follow the same organization/resource policy as HTTP.
- Run the smallest relevant checks during implementation. Explain any broader release qualification before running it. Do not run tests repeatedly when the same evidence remains valid.
- New business/financial/retention/delegation policies require the explicit decisions listed in the spec; a task cannot invent them to make tests pass.
- Future implementation starts from freshly fetched `origin/develop` in a clean isolated worktree and neutral branch. Preserve user work. Do not execute every phase on one long-lived stale branch.

## Review Focus

1. Access revoked between enqueue/connect and execution/delivery: current authority must win. Owned by phase 1 task E2 and phase 2 task S2.
2. Crash after one consumer commits but before another succeeds: retry only unfinished effects. Owned by E3; external effects remain at-least-once.
3. An idempotency key reused for a different payload, actor or organization: reject mismatch or keep scopes separate. Owned by S3.
4. Module disabled while work, live connections and retained rows exist: remove its behavior without deleting data or leaking stale delivery. Owned by E4, S2 and release task R2.
5. Billing plan/price changed or an event repeated after invoice issuance: historical amounts and one-invoice-per-period remain stable. Owned by B1/B2.

These cases have named behavioral tests in the owning plans. Review them in the existing single review pass, using the supplemental ECC checklist; do not start a separate generic audit.

---

## Phase sequence and deliverables

| Phase | Focused implementation plan | Depends on | Usable exit milestone |
| --- | --- | --- | --- |
| 0 | Architecture decisions and contract freeze below | Current source baseline | Ownership, system-work authority, dependency choices and compatibility rules are reviewable and approved for the first slice. |
| 1 | [Execution, events, jobs and email](2026-10-23-parity-01-execution.md) | Phase 0, D1; D5 for chosen scheduler adapter | An authorized organization operation can enqueue work, survive restart, consume once per database effect and expose delivery status. |
| 2 | [Cache, realtime, idempotency, flags, quotas and permission upgrades](2026-10-23-parity-02-platform-services.md) | Phase 1 contracts; S1/S3 can proceed after phase 0 | Shared services enforce current context across multiple replicas; existing Owners receive audited module-permission reconciliation. |
| 3 | [Documents and attachments](2026-10-23-parity-03-documents.md) | E2/E4; S5 for storage quota; D3 for purge/scanner policy | Existing Documents plus reusable authorized attachments, scan state and safe cleanup. |
| 4 | [Notifications](2026-10-23-parity-04-notifications.md) | E3/E4 and S2; E5 for optional email | Durable inbox and bell work end to end; realtime is recoverable through HTTP history. |
| 5 | [Webhooks](2026-10-23-parity-05-webhooks.md) | E3/E4, S3 | Explicitly exported events produce signed, retried, observable organization deliveries. |
| 6 | [Billing and organization lifecycle](2026-10-23-parity-06-billing.md) | E3/E4/E5, S3/S4/S5; phase 4 optional; D2 | Configured plans, subscription terms, immutable invoices, usage and organization lifecycle administration. |
| 7 | [Identity, organization branding and audit](2026-10-23-parity-07-administration.md) | E2/E4/E5; S1/S2 where used; D3/D4 for affected tasks | Session/group administration, optional password policy, governed support delegation, branding and audit investigation. |
| 8 | [Catalog](2026-10-23-parity-08-catalog.md) | Phase 3, S3 | Products/brands/categories, images, stock and recoverable lifecycle work through API and React. |
| 9 | [Tickets](2026-10-23-parity-09-tickets.md) | E3, S3; phase 4 optional | Real support state machine with conflict handling, assignment, comments and history. |
| 10 | [Chat](2026-10-23-parity-10-chat.md) | Phases 3/4, S1/S2/S3 | Authorized messaging, attachments, mentions, threads, reactions, pins, search and typing. |
| 11 | [Packaging and release qualification](2026-10-23-parity-11-release.md) | Completed slices selected for a release | Renamed React/backend-only applications and optional module combinations are qualified with real operations evidence. |

Numbers express a recommended delivery order, not a forced serial queue. Documents, Notifications and Webhooks can progress independently after their contracts land. Catalog and Tickets can proceed without waiting for Billing/identity policy work. Chat follows the most shared dependencies and is deliberately last among new business modules.

Each phase is several reviewable work packages; no credible calendar estimate is established by this document. Estimate E1-E4 first, then use measured delivery effort and resolved policy scope to forecast later phases. Do not convert phase count into a promised completion date.

## Phase 0: Freeze the architecture and first slice

### Task P0.1: Reconcile baseline and approve contract decisions

**Files:**
- Read: `templates/trykatch/AGENTS.md`, `templates/trykatch/docs/development/specifications.md`, `templates/trykatch/docs/modules.md`, `templates/trykatch/docs/module-data-isolation.md`.
- Read: `templates/trykatch/src/Common/Trykatch.Modules.Abstractions/TrykatchModule.cs`, `TrykatchDataResourceRules.cs`, and `templates/trykatch/src/Common/Trykatch.Infrastructure/Organizations/RuntimeDatabaseAccessProfiles.cs`.
- Create at execution: `templates/trykatch/docs/specs/feature-parity-contracts.md` and new ADRs for execution authority, event consumption, operational grants and adapter choice. Allocate unused ADR numbers at execution; do not reuse an existing number.

**Interfaces:** Consumes the existing contracts and this design. Produces the contract ledger naming DTOs, methods, ownership, permissions, event versions and allowed execution policies used by E1 onward.

- [x] Confirm source still matches the baseline table; reconcile any subsequent `develop` changes before assigning tasks.
- [x] Resolve D1 for a narrowly scoped synthetic operation and the first real consumer; record worker/coordinator grants and organization work authorization separately.
- [x] Resolve D5: compare a maintained PostgreSQL-backed job adapter with extending existing worker machinery; select the smallest supportable implementation after license/security/version review. Choose cache/backplane adapters without changing module APIs. Record failover and no-adapter behavior.
- [x] Freeze the contracts from spec section 4, including a typed failure/result vocabulary, trusted context provenance and cancellation semantics; remove speculative methods with no phase caller.
- [x] Record legacy event-name mappings and treatment of old logging-only records. Prohibit automatic replay/backfill of previously processed history.
- [x] Review five scenarios from Review Focus against the ledger. Each must have a named test owner and no authority inferred from payload strings.
- [x] Exit: D1/D5 decisions needed for the first implementation slice are approved, source references resolve, and no implementation task depends on an unnamed interface.

### Task P0.2: Define acceptance slices, permissions and module selection

**Files:** Create at execution `templates/trykatch/docs/specs/feature-parity-acceptance.md`; extend future module specs from each focused plan. Existing catalog: `templates/trykatch/trykatch.modules.json`; generator: `templates/trykatch/tools/Trykatch.ModuleTool/ModuleScaffolder.cs`.

**Interfaces:** Produces stable module IDs, permission IDs, safe default grants, optional dependency rules and a release coverage ledger. Consumes the contract ledger.

- [x] Use module IDs `notifications`, `webhooks`, `billing`, `catalog`, `tickets`, `chat`; keep existing `documents`. Decide module descriptors and dependency edges before scaffolding.
- [x] Preserve existing Projects/Documents defaults. New sensitive permissions default to deny for non-Owners unless the feature spec explicitly grants them. Notification/session self-service derives recipient/user from the current principal.
- [x] Agree an opt-in module selection flow using the existing catalog/CLI. New module source can be bundled disabled; enabling code remains explicit and atomic.
- [x] Document that existing `--documents` selects optional spreadsheet/PDF exporters, not the Documents business module; `--storage false` can disable its storage adapter. New flags/profile names must not change either meaning.
- [x] Specify behavior when an optional dependency is absent: Catalog image features require Documents; Tickets notifications are optional; Notifications cannot require Chat just to consume mentions when Chat is installed.
- [x] Validate adapter capability/configuration without requiring live external credentials or network probes during template generation/build. Runtime readiness and integration qualification prove actual storage/cache/backplane availability separately.
- [x] Add a parity coverage row for each spec subsystem and every D1-D8 decision; use `planned`, `implemented`, `verified`, `deferred` separately.
- [x] Exit: first slice is executable without choosing financial/retention rules prematurely; deferred mechanism parity is visible and cannot be counted as delivered.

## File and responsibility map

Paths below are relative to repository root. New paths in focused plans are proposals, not current files.

| Responsibility | Existing owner | Proposed additions |
| --- | --- | --- |
| Module-neutral contracts | `templates/trykatch/src/Common/Trykatch.Modules.Abstractions/` | Focused `Execution/`, `Eventing/`, `Jobs/`, `Realtime/`, `Caching/`, `Features/`, `Quotas/`, `Attachments/`, `Mailing/` contract files. |
| HTTP integration | `templates/trykatch/src/Common/Trykatch.Modules.AspNetCore/` and API security/composition | Scoped idempotency, exported realtime audience gate and endpoint policies. |
| Host adapters/security | `templates/trykatch/src/Common/Trykatch.Infrastructure/`, `Trykatch.Identity/`, API | Deep execution boundaries, role grants, persistence, cache/backplane/scheduler adapters and identity behavior. |
| Business capabilities | `templates/trykatch/src/Modules/<Module>/` | Five projects plus optional `Web/`, manifest and module-local tests for each new module. |
| UI composition | `templates/trykatch/web/packages/module-sdk/`, `web/apps/web/src/shell/` | Named shell contribution points and shared hooks; each module owns its own pages and messages. |
| Runtime/schema proof | `templates/trykatch/tests/Trykatch.IntegrationTests/` | Focused production HTTP/PostgreSQL/async/realtime acceptance tests, reusing runtime-role fixtures. |
| Distribution and operations | `scripts/`, `Trykatch.Templates.csproj`, template Compose/Aspire/docs | Renamed application matrices, opt-in selection, migrations, optional adapters, dashboards and recovery runbooks. |

The organization-only generator can create initial source for a suitable simple aggregate. Billing's mixed ownership, polymorphic attachments and cross-record workflows require manual/custom implementation. A generated status enum is not evidence of a state machine or concurrency guard.

## Common execution contract for every work package

1. Read the linked spec and prior task Interfaces. Replace proposed path names only when the existing source convention requires it; update the contract ledger and downstream plans in the same change.
2. Add the named behavioral cases using the existing test fixtures, run only those new/affected checks, implement the slice, and rerun the focused checks. Include directly affected existing regressions; never weaken the assertion to obtain a pass.
3. Extend schema declarations, live-grant inspection and forward migrations together. Generate timestamped migration filenames at execution; never edit previously applied migrations or create misleading old timestamps from this planning date.
   New module permissions also require S6 reconciliation for organizations that already exist; passing tests only against freshly seeded organizations is insufficient upgrade evidence.
4. For API/UI work, build OpenAPI, generate clients and inspect generated changes. Add Storybook states, English/French strings, permission/error/conflict behavior and a real browser flow.
5. Record observed test results and not-run checks in the phase evidence record, get one integrated review, fix accepted findings, and create a neutral project-focused commit. No AI attribution. Commit only that task's intended changes; pushing/PR publication is a separate authorized action.
6. Re-run only checks affected by subsequent fixes. A completed task has a usable slice plus evidence, not unchecked infrastructure stubs.

## Targeted verification command catalog

Run application commands from `templates/trykatch/`, or the renamed generated application's root. These commands are planned; they have not been run for the additions. `<Class>` and `<Module>` are explicit placeholders replaced by the owning task names.

```bash
# New/changed unit behavior; compile changed tests (do not add --no-build).
dotnet test tests/Trykatch.UnitTests --filter FullyQualifiedName~<Class>
dotnet test tests/Modules/<Module>/Trykatch.Modules.<Module>.UnitTests

# Real PostgreSQL + production HTTP behavior; Docker is required.
dotnet test tests/Trykatch.IntegrationTests --filter FullyQualifiedName~<Class>

# Composition changes: Debug build is required for host architecture checks.
dotnet build Trykatch.slnx
dotnet run --project tools/Trykatch.ModuleTool --no-build -- module doctor
dotnet test tests/Trykatch.ArchitectureTests --no-build
dotnet test tests/Modules/<Module>/Trykatch.Modules.<Module>.ArchitectureTests --no-build

# API/UI contract changes, only where web/package.json exists.
corepack pnpm --dir web generate
corepack pnpm --dir web generate:check
corepack pnpm --dir web typecheck
corepack pnpm --dir web/apps/web exec vitest run src/<affected-test>.test.tsx
corepack pnpm --dir web/apps/web exec playwright test e2e/<affected-flow>.spec.ts
corepack pnpm --dir web build
```

Module-local UI: use `corepack pnpm --dir src/Modules/<Module>/Web exec vitest run` with the affected test filename. Adapt the exact command to that package's committed script; do not treat an absent test runner as a pass. Storybook adds a focused run via `web/apps/storybook` plus `storybook:build` when its wiring changes.

Existing regressions to select when relevant: `PostgresRlsTests`, `GeneratedOrganizationIsolationTests`, `PostgresIsolationInspectionTests`, `ModuleActivationTests`, `ModuleAuthenticationBoundaryTests`, `ModuleAntiforgeryTests`, `AuditIntentAtomicityTests`, `OutboxRecoveryIntegrationTests`, `AtomicMutationResponseIntegrationTests`, `AuthenticationSecurityTests`, `AccountSecurityStepUpTests`, `ModuleProjectGraphTests`, `ModuleWorkspaceTests` and `ModuleScaffolderTests`.

Success means the named tests executed and passed with the expected assertions; no skipped PostgreSQL test counts as isolation evidence. Record test counts from actual output, never from this plan. Full release matrices occur only in phase 11 or when a cross-cutting change makes targeted evidence insufficient.

## Rollout and rollback

- Release host contract changes compatibly before optional modules consume them; update declared host compatibility only when required. Preserve existing serialized messages with explicit version readers.
- Ship each new module disabled by default. Enable in an isolated generated app, then a deployment-owned staging environment, then selected organizations through runtime availability after code is installed.
- Use expand-first schema changes and backward-compatible readers. Roll back code only while its version can read the new schema; otherwise pause workers/module behavior and roll forward. Do not promise destructive down migrations.
- On a bad consumer, pause only its delivery/affected module; preserve pending and terminal records, inspect sanitized diagnostics, and replay explicitly after correction.
- On cache/backplane failure, preserve durable writes and allow HTTP reconciliation; never make cache correctness or a dropped push the authorization boundary. Hard quota enforcement fails closed if durable reservation cannot be established.
- Feature flags cannot remove already issued invoices, erase chat history, change stored grants or replace module migration/disable semantics.

## Deferred workstreams and decision register

Dedicated PostgreSQL, presigned/public storage and cloud IaC are separate optional tracks under phase 11, with named acceptance gates. They are needed for matching particular mechanisms/deployment choices, not for changing the default architecture. Billing commercial rules, content/retention policy and support delegation remain the D2/D3/D4 decisions in the spec.

The plan does not certify regulatory compliance, load capacity or production readiness. Preserve the existing [readiness plan](../../production-readiness-plan.md), add evidence for changed surfaces, and report remaining gates explicitly.
