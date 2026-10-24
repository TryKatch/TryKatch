# Phase 1 execution evidence

Status: Consolidation in progress. E1–E5 slices are implemented and their evidence is linked below. E5 is merged after final-head CI. The installed Projects event and separate synthetic operation OS-process exit is being implemented; it is not yet claimed verified here.

## Qualified slices

| Slice | Recorded evidence | Scope |
| --- | --- | --- |
| E1 contracts and ownership | [E1](parity-execution-e1.md) | Explicit versioned execution/event contracts, manifests and operational relation ownership. |
| E2 validated organization execution | [E2](parity-execution-e2.md) | Current membership/system authority, transaction-local scope, audit, completion recovery and real PostgreSQL isolation. |
| E3 durable consumer delivery | [E3](parity-execution-e3.md) | Transactional fanout, per-consumer delivery/inbox, source/schema/current authority validation and legacy fallback. Provider recreation checks are not OS-process restart proof. |
| E4 durable jobs | [E4](parity-execution-e4.md) | Opt-in clustered Quartz/PostgreSQL bridge, durable host ledger, recurring controls, role/migration inspection and UI. PR #131 merged after CI. |
| E5 approved email | [E5](parity-execution-e5.md) | Separate organization/Identity stores, protected intent, current disclosure checks, bounded SMTP and safe operators. PR #132 merged after exact-head CI and real production restart/two-replica qualification. |

Earlier dependency-advisory blockers in E1/E3 records describe their historical checkpoints. E4 patched that baseline; E5 final CI audits report no vulnerable packages. Historical evidence must not be read as a new clean result for those old revisions.

## Final E5 revision

Final source `a21c9b161d87e3b9a8c7eae7bb26ea665b6c2532` passed [template CI](https://github.com/TryKatch/TryKatch/actions/runs/40056611054) and [dependency review](https://github.com/TryKatch/TryKatch/actions/runs/40056611017). Its strict builds reported zero warnings/errors; 631 host unit, 27 architecture, 16 module unit and 399 integration cases passed with zero skips. All 168 Storybook interaction/accessibility cases passed. Generated product matrices, both production profiles, deployment, image, schema, documentation, observability and supply-chain gates passed.

[PR #132](https://github.com/TryKatch/TryKatch/pull/132) merged at `0c122fe30d34feaef52c64709956ed1c08591554`; comparing fetched develop with the tested head returned no tree differences. Phase 1 exit work starts from this commit in a fresh isolated branch.

## Installed event and synthetic operation

This remaining qualification uses an actual installed Projects creation operation/event and a test-only consumer/effect module. Its separate synthetic job is a current-user command; the event consumer exercises finite system-grant authority. Public production composition/executor/scheduler/hosted services are used by a dedicated test process. Parent-only setup owns migration credentials; child processes use the real named runtime roles. This is application/executor/process evidence, not HTTP/browser evidence.

Implementation and process/role results will be recorded here when run. Until then the Phase 1 process exit, compatibility rollup and final integrated review remain open. No default release or later parity phase is accepted by this document.
