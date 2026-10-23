# Feature parity Phase 0 evidence

Status: Documentation implementation completed; final review/validation record is maintained below. New runtime features remain unimplemented.

## Scope and baseline

The user authorized implementation beginning with Phase 0. P0.1 establishes the contract/architecture baseline; P0.2 establishes permissions, optional-module behavior and falsifiable acceptance. Phase 1+ runtime implementation is outside this change.

Fetched `origin/develop` on 2026-10-23 and confirmed it remained at `d3e8dc40b6c5d4d20a173ec2f884353f4f25a7d9`. A new clean isolated worktree/branch was created from that commit. The earlier uncommitted planning worktree was preserved; its 13 authorized plan/design documents were copied and committed as planning baseline `fb3cd62`. No user work was stashed, reset or overwritten.

Application source, dependency locks, template flags, database schema/grants and runtime configuration remain unchanged. The documents describe planned interfaces; they do not create executable implementations.

## Deliverables

| Task | Artifact | Established result |
| --- | --- | --- |
| P0.1 | [Contract ledger](../feature-parity-contracts.md) | Source baseline, v1 shapes/callers, host-only execution boundary, event compatibility, first executable slice and evidence owners are explicit. |
| P0.1 | [ADR 0015](../../adr/0015-bounded-background-execution.md) | D1 architecture selected; user commands, committed-event effects and scheduled grants are distinct; D4 remains separate. |
| P0.1 | [ADR 0016](../../adr/0016-durable-module-event-consumption.md) | Atomic fan-out, per-consumer inbox, post-commit dispatch and no implicit historical replay. |
| P0.1 | [ADR 0017](../../adr/0017-operational-data-access-profiles.md) | Closed role/table ownership and transaction boundaries; no generic CRUD/BYPASSRLS or writable-global plan shortcut. |
| P0.1 | [ADR 0018](../../adr/0018-scheduler-cache-and-realtime-adapters.md) | D5 library/provider architecture selected with license/framework evidence and explicit qualification gates. |
| P0.2 | [Acceptance/permission matrix](../feature-parity-acceptance.md) | Optional combinations, finite permission IDs/defaults, current-source baseline, planned test layers and unresolved business gates. |

Maintainer documents are repository-level context outside generated applications: `docs/superpowers/plans/2026-10-23-feature-parity-plan.md` and `docs/superpowers/specs/2026-10-23-feature-parity-design.md`. Application-local ledger/ADRs/matrix are self-contained and authoritative for the frozen Phase 0 baseline.

## Source reconciliation findings

- Current production Owner grants are seeded snapshots and resolved from persisted roles. `Role.AddMissingPermissions` exists and demo startup repairs demo role defaults; that is not production-wide upgrade reconciliation. S6 explicitly covers existing organizations without broadening non-Owners.
- The default outbox transport only logs. Existing context/name compatibility and replay IDs are retained; scope cannot be guessed for old records to trigger new effects.
- Current ownership/grant/connection profiles are closed. New operational records and a coordinator role require explicit migrations, provisioning and inspector proof; Phase 0 changes none of them.
- Current Documents permissions alone do not model private-owner attachments. Future F1 covers every existing Documents route, not just an attachment helper.
- Existing `--documents` selects spreadsheet/PDF exporters and `--storage false` independently disables storage adapters. Phase 0 chooses the existing module catalog/enable/generate workflow and no new CLI switch.
- Existing cache/search research already favors scoped HybridCache and Valkey. ADR 0018 reuses that direction while keeping PostgreSQL business/idempotency/quota state authoritative.

## Direct dependency research — 2026-10-23

Read official NuGet package registration/catalog metadata for these exact candidates. Each was listed, had no deprecation object, and reported no vulnerability entries in the returned metadata. This is a direct-metadata observation only: it is **not** a restore, transitive vulnerability audit, compatibility test, license attestation for an image or production qualification.

| Candidate | Reported SPDX | Metadata observation |
| --- | --- | --- |
| Quartz `4.6.0` | Apache-2.0 | Listed; package targets .NET 10; PostgreSQL support documented by project. |
| Microsoft.Extensions.Caching.Hybrid `10.11.0` | MIT | Listed; runtime/package graph not resolved here. |
| Microsoft.Extensions.Caching.StackExchangeRedis `10.0.11` | MIT | Listed; version alignment remains an implementation check. |
| Microsoft.AspNetCore.SignalR.StackExchangeRedis `10.0.11` | MIT | Listed; no claim of tested Valkey/backplane integration. |
| StackExchange.Redis `3.4.20` | MIT | Listed; observed candidate only, not an unconditional transitive override. |

Sources and operational limitations are linked in ADR 0018. The NuGet v3 registration source for each is `https://api.nuget.org/v3/registration5-gz-semver2/<lowercase-package-id>/<version>.json`, followed to its catalog entry. The catalog response is gzip-encoded and was decoded before inspection. No packages were installed from this research.

## Validation record

| Check | Result | Limit |
| --- | --- | --- |
| Fresh upstream/source reconciliation | Passed: baseline unchanged; current relevant code inspected. | Does not execute future contracts. |
| Local Markdown file targets, whitespace and fenced-code structure | Passed across 20 plan/specification/Phase 0 documents; no broken local file targets or whitespace/fence issues. | Structural documentation checks, not runtime proof. |
| Generated-app documentation boundary | Passed: all seven new app documents and their local file targets exist inside each renamed output. | React and backend-only payloads; no generated application build. |
| Phase 0 permission/contract consistency | Passed: 33 distinct listed permission IDs match existing grammar/length limits; ledger, ADRs, matrix and affected plans reconciled. | Static validation/review only; no runtime authority claim. |
| Independent architecture/document review | Approved after fixing transaction-before-membership sequencing and actor/intent-scoped job request deduplication. | One integrated review, followed by scoped confirmation of its two findings; no duplicate audit. |
| Packed renamed React/backend-only documentation smoke | Passed with isolated official SDK `10.0.301`: template pack, custom-hive install and both renamed payloads. | Checks documentation payload/replacement/link integrity, not generated application compilation or runtime readiness. |
| Runtime feature/unit/PostgreSQL/browser/performance checks | Not run: no product code changed and Phase 1+ is unstarted. | Planned tests remain unimplemented/unverified. |

No broad application suite was run for these documentation changes. The template pack compiles only the packaging project (application source is excluded). The default machine SDK lacked the required feature band; official SDK `10.0.301` was downloaded into ignored task scratch and SHA-512 verified against official release metadata. No global SDK/configuration or `global.json` change was made. An initial generation with scripts disabled returned CLI exit `105` for the suppressed Git-initialization post-action while creating both payloads; final generation allows that inspected local-only action.

Reproduction uses the required SDK: `dotnet pack Trykatch.Templates.csproj --configuration Release --output <scratch>/packages` with intermediate/output paths redirected to scratch, `dotnet new --debug:custom-hive <scratch>/template-hive install <package> --force`, then `dotnet new --debug:custom-hive <hive> trykatch --name PhaseZero.FullStack --display-name "Phase Zero" --output <scratch>/generated-fullstack --allow-scripts yes` and the equivalent `PhaseZero.Backend` command with `--ui none`. Seven Phase 0 documents, local file targets and expected presence/absence of `web/package.json` were inspected in each output. Scratch commands/results are local verification aids, not shipped application code.

## Remaining decisions and next action

D1 and D5 are technical architecture decisions, not implemented runtime features. E1/E2 and E4/S1/S2 still own their package/privilege/behavior qualification. D2 billing policy, D3 retention/scanning policy and D4 support delegation remain unresolved; D6 dedicated placement, D7 presigned/public storage and D8 cloud IaC remain separate tracks. Their specific acceptance claims stay deferred without blocking independent work.

The next implementation slice is Phase 1 E1, using the ledger's first-slice scope and explicitly compatible envelope/registry/ownership work. No Phase 1 task is marked complete by this Phase 0 record.
