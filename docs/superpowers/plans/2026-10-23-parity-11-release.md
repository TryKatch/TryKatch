# TryKatch Packaging and Release Qualification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task; retain targeted implementation tests and one integrated review workflow.

**Goal:** Ship selected completed capabilities as optional, renamed, operationally qualified TryKatch applications with honest parity evidence.
**Architecture:** The same catalog controls API/migrator/Web/worker composition. Template options choose code/adapters at generation/build time; runtime flags only affect already installed behavior.
**Tech Stack:** Existing template/CLI, NuGet/npm manifests/locks, Compose/Aspire, CI and deterministic fixtures. Optional deployment adapters require an explicit target decision.
**Spec:** [Design and D6–D8](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths below are relative to repository root. This phase is the deliberate broader qualification gate: shared contracts, migrations, optional modules, backend-only generation and multiple runtime replicas interact, so isolated module tests cannot establish release compatibility. Explain the selected matrix before executing it; do not rerun every matrix on routine task edits.

## Review Focus

- A generated application must work under a different technical namespace/display name and without frontend source.
- Optional modules, source flags and storage/email adapters must not create hidden dependency cycles or missing project/package references.
- Disabled modules leave historical migrations/data readable to allowed maintenance but remove API/UI/consumer/live behavior.
- Restore/replay after a real outage preserves organization isolation and deduplication.
- Deferred mechanisms and unresolved readiness gates must not be reported as completed parity.

## R1: Extend template/module packaging and documented selection

**Files:** `templates/trykatch/.template.config/template.json`, `Trykatch.Templates.csproj`, `templates/trykatch/trykatch.modules.json`, module manifests, module-tool lifecycle/scaffolder/facts/help, solution/project/package graphs/locks, source conditions, `scripts/test-template.sh`, `scripts/test-generated-application.sh`, `templates/trykatch/scripts/test-module-packages.sh`, relevant source/CLI tests and docs-site pages.

**Interfaces:** Existing `module enable/disable/install/doctor/facts` remain the lifecycle surface. Phase 0 selects that workflow and explicitly adds no generation-time `--modules` flag; a convenience profile is documentation over the same catalog, not another installer. Preserve existing `--documents` exporter semantics. New module source defaults disabled; full parity profile is explicit.

- [ ] Add selection/disable/upgrade tests proving generated backend/API/migrator/Web/worker agreement, exact paired package versions and stable renamed event contracts. Host compatibility ranges reflect actual new required contracts.
- [ ] Add required dependency validation for Catalog→Documents and Chat→Documents/storage; preserve optional Notifications integration without cycles. Do not silently activate unrelated modules when a flag changes.
- [ ] Preserve independent optional email/storage/exporter/image flags. Backend-only generation omits Web artifacts/references/commands while retaining selected backend modules. Verify both `Acme.Operations` technical naming and a separate display name.
- [ ] Run targeted module-tool contract/scaffolder/package tests first. Then pack once and run the affected existing generated-application/option scenarios, extending those scripts with new scenario selectors rather than adding a second competing qualification harness.
- [ ] Exit: generated default, selected module and backend-only artifacts restore/build/run under the documented prerequisites; record exact artifact hash/commit and no generated drift.

## R2: Qualify supported combinations, failures and operations

**Files:** New integration/browser scenarios under existing test owners; `.github/workflows/ci.yml` and `scripts/ci/` only for required scopes; template Compose/AppHost adapter configuration; `templates/trykatch/deploy/observability/` dashboards/alerts; operator runbooks and `docs/production-readiness-plan.md` evidence links.

**Interfaces:** Release coverage ledger records planned/implemented/verified/deferred separately for every feature and selected combination. Use existing SBOM/license/dependency/release-artifact machinery rather than a new release pipeline.

- [ ] Execute a selected matrix: baseline React, baseline backend-only, full parity React, full parity backend-only, Notifications alone, Webhooks alone, Billing without Notifications/email, Tickets without Notifications, Catalog+Documents, Chat+Documents without Notifications and all selected modules with email/storage configured.
- [ ] Add negative generation/composition cases for missing Documents/storage, unknown module IDs, duplicate features/consumer keys, unsupported host versions and incompatible graph versions. Test representative disable/re-enable and upgrade paths for every new module; do not claim all exponential combinations were tested.
- [ ] Run real PostgreSQL runtime-role/raw-SQL isolation, production HTTP, async restart/dedup, backplane/cache outage, revoked actor/session, quota race and safe network-destination tests. Add one coordinated multi-replica run because per-process mocks cannot establish distributed behavior.
- [ ] Exercise deployment-owned backup/restore, schema upgrade and worker pause/replay. Verify dashboards for event/job age/backlog, terminal deliveries, retry rates, storage/scanner failures, quota rejects, realtime disconnects and invoice-period generation. Do not export payloads/credentials to metrics/logs.
- [ ] Record a reproducible workload and report measured latency/throughput/resource use with configuration and limits. Establish release targets from the intended deployment and measurements; do not invent universal capacity/SLO claims from module tests.
- [ ] Broaden to existing required release gates only once the selected integrated candidate is ready. Review skipped tests, exact-commit CI, generated containers, dependency/license/SBOM/secret checks, accessibility and existing outstanding readiness gates; no blanket readiness checkbox based on green unit tests.
- [ ] Exit: selected coverage is reproducible, restore/replay has evidence, rollback/pause paths are tested, and omissions are reported explicitly.

## R3: Close documentation and functional parity ledger

**Files:** Root README and public docs-site architecture/modules/operations/reference pages; generated-app developer guides, onboarding/assistant knowledge where factual module descriptions changed; per-phase evidence files and the existing readiness ledger.

**Interfaces:** One factual feature matrix links each row to implemented source, API/UI behavior, immutable validation evidence and limitations. Existing assistant execution remains unchanged unless separately specified.

- [ ] Recheck the linked FullStackHero documentation for scope changes; distinguish our comparable behavior from exact provider/protocol/project-layout parity.
- [ ] Document first-run, enabling a module, dependency errors, permissions, backend-only use, module disablement, delivery semantics and operations recovery. Update English/French public/user docs for shipped flows.
- [ ] Confirm a new developer can follow a renamed app from installation to one Notification/Webhook/Ticket flow using the documented commands. Confirm no copied canonical skill/AGENTS text or unsupported implementation claims entered memory/docs.
- [ ] Run focused link/command/reference checks and the affected docs-site build; retain a concise evidence/not-run report. Commit the reviewed documentation with the matching release slice.

## Optional R4: Dedicated PostgreSQL placement track — D6

**Scope:** Reuse existing `IOrganizationDataPlacement` and the prior [isolation plan](../../plans/module-data-isolation-plan.md) while reconciling it with current source. Current `CreateOrganization` explicitly rejects dedicated placement; changing a UI control is insufficient.

**Proposed files:** Existing placement adapters/router, tenant creation workflow, organization context/DbContext factory, migrator/worker topology, role/secret provisioning, backup discovery and dedicated-placement integration fixtures. Business module APIs remain unchanged.

- [ ] Specify provider/secret provisioning, immutable placement and migration-version policy. Implement asynchronous idempotent provisioning, per-database schema/grants/probes, route registration and owner invitation only after readiness.
- [ ] Route HTTP/jobs/events/audit/outbox/usage/attachments correctly and preserve tenant assertions/RLS. Define exactly which control-plane data remains shared and how atomicity/delivery changes across physical databases.
- [ ] Qualify two dedicated databases plus shared placement, failure/retry, schema upgrade, backups/restores and wrong-route attacks using real credentials. No migration of existing organization placement is included without its own data-move/rollback design.
- [ ] Mark dedicated mechanism parity verified only after this end-to-end evidence; it is not a prerequisite for default shared-RLS functional modules.

## Optional R5: Direct/public storage access track — D7

**Scope:** Separate ADR for changing the current no-raw-object-key/API-streaming promise. Catalog/Chat must work with existing opaque authenticated attachments before this track begins.

- [ ] Specify path disclosure, public visibility, bounded presign lifetime, owner authorization, checksum/content-length checks, PendingUpload→Quarantine→Available state, finalize idempotency and orphan cleanup.
- [ ] Implement adapter support through Documents' public contract; URL issuance and finalization independently revalidate current authority and quota. Storage keys/credentials are not accepted from browser input.
- [ ] Qualify revoked/expired URLs, incomplete upload, duplicate finalize, spoofed content, scan rejection, public/private transition, cross-organization keys and cleanup after failure. Preserve an API-mediated fallback and document signed-URL revocation limits.

## Optional R6: Cloud-provider IaC track — D8

**Scope:** Add only after selecting a real deployment target. Existing single-host Compose remains supported.

- [ ] Specify network/TLS ingress, secret/key management, PostgreSQL/object store/cache/backplane services, migration ordering, worker topology, telemetry retention and backup/restore ownership.
- [ ] Create provider-owned IaC under a named `deploy/<provider>/` directory, with plan/validation checks and deployment runbooks. Applying infrastructure is a separate authorized external action.
- [ ] Qualify a disposable target, least-privilege credentials, restart/restore, upgrade and cost/resource visibility. Do not infer target-cloud topology or capacity from generic architecture examples.

## Final release exit

- [ ] Functional feature rows have implementation and runtime evidence; D6–D8 mechanisms are either separately verified or explicitly deferred.
- [ ] Commercial/retention/support policy gates are resolved for the selected release or the affected workflows remain unavailable and unclaimed.
- [ ] No module weakens kernel controls, optionality, generated-client ownership or data-retention guarantees.
- [ ] Report exact checks and remaining limitations; request implementation/release decisions only when the next action actually requires them.
