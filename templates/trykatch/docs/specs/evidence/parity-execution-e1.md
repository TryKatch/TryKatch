# Phase 1 E1 — execution contracts and operational ownership

Historical E1 checkpoint; subsequent E2 implementation is recorded in [E2 evidence](parity-execution-e2.md).

Status at E1 completion: E1 implementation and targeted validation complete; the pre-existing standard dependency-audit gate remains failing as recorded below. E2–E5 remain unimplemented. This slice does not deliver background business execution, consumer fan-out, a scheduler or queued email.

## Scope and compatibility

Started from freshly fetched `origin/develop` at `d3e8dc40b6c5d4d20a173ec2f884353f4f25a7d9` in isolated branch `feat/execution-contracts`. The Phase 0 planning/contract commits were carried forward without changing prior worktrees. The implementation follows the [contract ledger](../feature-parity-contracts.md), [ADR 0015](../../adr/0015-bounded-background-execution.md), [ADR 0016](../../adr/0016-durable-module-event-consumption.md) and [ADR 0017](../../adr/0017-operational-data-access-profiles.md).

- Explicit module operation, finite system-grant, event, consumer, job and recurring-schedule descriptors are validated before module activation. Host-only request/outcome/executor interfaces live in Application; no executor implementation is registered.
- Existing `IOrganizationModuleData.Enqueue<T>`, `IOutboxWriter.Enqueue<T>` and five-argument `OutboxEnvelope` construction remain supported. Projects/Documents wire names and processed/replay history are preserved. Legacy enqueue retains null metadata and the existing logging behavior; no historical scope is guessed or backfilled.
- The host-only `IOutboxWriter.Enqueue<T>(message, operationKey, operationVersion)` captures a resolved current membership, registered UserCommand operation and explicitly typed event. Organization, actor and membership come from host context, independently of payload properties. E1 does not support system-origin submission. Resource authorization and execution-time membership revalidation remain E2 responsibilities.
- Outbox columns `ContextVersion`, `ContextJson` (JSONB) and `PayloadHash` are nullable for legacy compatibility. A v1 context contains organization, effective/origin actor, membership, execution kind, operation/event identities and versions, catalog fingerprint and correlation. Its SHA-256 payload hash uses recursive ordinal property ordering and lossless numeric normalization, so PostgreSQL JSONB normalization does not invalidate it.
- Malformed/incomplete metadata, unsupported context versions and hash mismatches terminalize without invoking transport, with sanitized recovery history. E1's reader validates context format and hash, **not active-catalog compatibility of queued operation/event identities**. E2/E3 must resolve registered versions before any business execution or fan-out. The default logging adapter creates no business effects, and previously processed logging-only history is not automatically replayed.

## Schema, ownership and deployment

Forward migration `20261023162558_AddModuleExecutionContracts` adds nullable outbox metadata and three host-owned foundations. It does not rewrite existing messages or activate consumers. EF snapshot/model consistency and upgrade preservation are covered by the targeted migration tests.

| Relation | Organization role | Outbox role | Work coordinator |
| --- | --- | --- | --- |
| `infrastructure.module_work_items` | INSERT, current organization/actor bound | None | SELECT / INSERT / UPDATE |
| `infrastructure.module_event_deliveries` | None | SELECT / INSERT | SELECT / UPDATE |
| `app.module_event_inbox` | SELECT / INSERT, forced organization RLS and actor-bound insertion | None | None |

All three relations use forced RLS and exact inspected role policies. The inbox uniqueness key is `(OrganizationId, MessageId, ConsumerKey)`; changing schema version cannot defeat deduplication. Work request uniqueness includes organization, trusted execution-identity key, job/version and RequestId; intent hashes and lease indexes support later processors. Coordinator credentials grant no business/Identity access, table ownership, schema creation or RLS bypass. Migrator provisioning and catalog inspection use the same closed access profiles.

The fifth fixed credential is `trykatch_work_coordinator` through `ConnectionStrings:trykatch-work`; migrator configuration is `Database:WorkCoordinatorRole`. Bootstrap SQL, AppHost, both Compose variants, example environment and affected smoke-script configuration supply it. Production validates five distinct fixed runtime users. A missing coordinator connection never falls back to `trykatchdb`.

Existing database volumes do not rerun PostgreSQL init files. Before upgrading, provision the new restricted login with a dedicated secret through the deployment's administrator/bootstrap process, configure its runtime connection and migrator role setting, then run the one-shot migrator to apply schema and exact grants. Review [database setup](../../database.md) and the bootstrap SQL; never substitute the privileged migrator credential in the API. No deployed database was changed by this task.

## Module manifest contract

Schema v1 accepts an optional `execution` object with default-empty `operations`, `systemGrants`, `integrationEvents`, `eventConsumers`, `jobs` and `recurringSchedules`. Existing manifests remain valid without it. Updated CLI doctor/facts validate and report declarative metadata without loading module assemblies, CLR types or payload readers. Strict unknown-member rejection is retained; an older CLI will reject a manifest using the new block and must be upgraded alongside it.

Manifest execution-kind tokens are `user-command`, `committed-event`, `scheduled-system`; misfire tokens are `coalesce`, `catch-up`. Owner IDs derive from the containing manifest. UserCommand `ResourcePolicyKey` names a resource policy and need not equal a table resource name; finite system grants remain constrained to declared resource keys and compatible owned operations. Scheduler syntax/provider qualification remains E4 work; no new template flag or module selection default is introduced.

## Validation record

Required SDK: `10.0.301`, reused from the SHA-512-verified isolated SDK prepared for Phase 0. Debug output is used for architecture checks. Docker-backed PostgreSQL tests ran against the committed PostgreSQL image; skipped checks never count as isolation evidence.

| Check | Observed result |
| --- | --- |
| Standard baseline restore/test gate | Failed before implementation: existing `System.Security.Cryptography.Pkcs` 10.0.0 NU1902/NU1903 advisories. Integration restore also reports existing `System.Security.Cryptography.Xml` 10.0.11 NU1903 advisories. |
| Unchanged-source diagnostic baseline | 19 targeted unit and 30 PostgreSQL/migration cases passed, zero skips. |
| Test-first evidence | New contract APIs absent; operational profile cases failed; new `execution` metadata/facts rejected; new schema foundations absent. Initial fixture/compile defects were fixed before interpreting behavioral results. |
| Targeted new/affected unit, CLI and reference-module cases | 98 passed, zero failures/skips. |
| Migration and PostgreSQL inspection cases | 34 passed, zero failures/skips, including old-history preservation, JSONB round trip, narrow grants, scope/actor denial and consumer deduplication. |
| Debug solution build | Passed with pre-existing dependency advisories visible. |
| Module doctor | Healthy; existing enabled/disabled module composition preserved. |
| Host and Projects/Documents/Federation architecture checks | 12 + 5 + 5 + 5 passed, zero skips. |
| Hosted outbox, audit atomicity and role-fixture regressions | 34 passed, zero failures/skips, including unsupported-context terminal recovery exactly once across restart. |
| Packed renamed React/backend-only smoke | Pack/install/generation passed; 41 focused contract, manifest, outbox-context and credential cases passed in each renamed output, zero skips. |
| Integrated code review | Approved for E1; no unresolved production findings. A hash-tamper test mutation was corrected during the same review. |
| Whitespace, JSON schema and edited shell syntax | Passed; final diff check repeated before commit. |

Diagnostic restores/builds used `-p:WarningsNotAsErrors=NU1902%3BNU1903`: audit remained enabled and its warnings remained visible. Dependencies, locks and security policy are unchanged. This adjustment isolates behavioral evidence; it does **not** make the standard restore/security gate pass or establish release readiness. Resolve the existing dependency advisories in a separately scoped dependency update before claiming a clean gate.

Run targeted application checks from the application root: `dotnet test tests/Trykatch.UnitTests --filter '<affected classes>'`, `dotnet test tests/Trykatch.IntegrationTests --filter 'FullyQualifiedName~PostgresIsolationInspectionTests|FullyQualifiedName~OutboxMigrationTests'`, the affected hosted regression filters, `dotnet build Trykatch.slnx`, `dotnet run --project tools/Trykatch.ModuleTool --no-build -- module doctor`, and the architecture projects named above. The exact filters and observed results are retained in task-local verification logs. No full application test suite, frontend/browser matrix, scheduler/provider qualification or E2 authorization execution test was claimed.

Next slice: E2's validated organization executor and current user/system authority, using these contracts and operational foundations. E3 still owns atomic fan-out/inbox effects and compatibility checks before invoking consumers.
