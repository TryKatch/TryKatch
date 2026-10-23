# Phase 1 E2 — validated organization work execution

Status: implementation, targeted validation and integrated review complete; the standard dependency-audit gate remains failing as recorded below. E3–E5 remain planned. No scheduler, fan-out adapter, email dispatcher or public work-submission endpoint is activated by this slice.

## Runtime boundaries

The host implements `IOrganizationWorkExecutor` with a fresh DI scope per attempt. Modules explicitly register one handler per operation/version and one current resource policy per policy key; startup rejects missing, duplicate or undeclared adapters. Handlers must defer data access until invoked and persist outgoing intents instead of publishing externally. There is no assembly scanning or caller-supplied method/type dispatch.

Stored v1 `ModuleWorkEnvelope` binds the organization, effective actor and operation to the coordinator row. Unknown context/catalog protocols, incompatible operation versions, malformed payloads/hashes and unsupported placement fail closed. An absent module or inactive organization pauses execution. Ready shared PostgreSQL placement is supported; dedicated placement, session and delegation frames remain unsupported. Captured provenance is never an authorization token.

- User commands resolve current membership and permission keys after beginning the existing actor-scoped transaction. Captured membership IDs do not grant authority. Revocation produces no business effect and an allowlisted denial audit.
- Committed events and scheduled system operations require the current installed finite grant, its fixed audit actor, exact operation/version and resource key. A system principal has only that operation's finite permission set; membership-only use cases throw. No Identity user, Owner role or sentinel membership is created.
- A committed-event attempt additionally verifies its durable source outbox record, org/origin attribution, identical canonical payload hash and an installed accepted event/schema. Origin membership revocation does not cancel already committed bookkeeping. The resource policy still checks current recipient/disclosure state on every effect attempt.

## Atomicity and recovery

`trykatch-work` is the only coordinator connection, with no privileged fallback. Claims use short row-lock transactions, a two-minute random-token lease, a one-minute execution cancellation deadline, five maximum business attempts (acknowledged pauses restore their claim budget) and a durable minimum five-second retry delay. Original schedule/intent values are retained. Expected-operation mismatches never modify legitimate work, and stale acknowledgements cannot overwrite another lease. Cancellation releases its claim through bounded cleanup; a failed cleanup leaves an expiring lease.

The business effect, `app.module_work_completions`, required audit intent and any outgoing intent commit in the same actor-scoped transaction. Application data enlists through the existing same-role transaction seam; automatic EF retries are rejected. Exhausted claims can only recover a matching completion marker; they never invoke a sixth handler. An organization/work advisory transaction lock serializes competing attempts. Coordinator acknowledgement happens after that commit. Recovery checks the host completion record under forced RLS before resolving current membership and resource authorization, since it does not run another handler or disclose business data. A denial rolls back the entire attempt before writing its sanitized audit in a fresh actor-scoped transaction.

Forward Application migration `20261023170617_AddOrganizationWorkExecution` adds nullable `NextAttemptAtUtc` and the organization-owned completion relation. Platform migration `20261023171629_AllowWorkAuditProvenance` extends only the audit detail key allowlist for origin, execution kind and named system actor; scalar/size/isolation constraints stay in force. Its downgrade preserves historical append-only rows while enforcing the older keys on new inserts. Its key is `(OrganizationId, WorkId)`; operation/version, effective actor and payload hash must match on recovery. Only the organization role receives SELECT/INSERT, with forced organization RLS and actor-bound insertion; coordinator, platform, Identity and outbox roles receive no completion access. Provisioning and exact policy inspection share the closed host profile. Existing databases require the one-shot migrator to apply schema and grants before runtime rollout; no deployed database was changed here.

## Outgoing compatibility

Legacy messages and v1 UserCommand contexts retain their existing behavior and wire shape. A validated system execution frame can append an explicitly registered event only for its current operation; it emits v2 provenance with the system effective actor, optional original actor and null membership. The reader distinguishes v1 user and v2 system formats and validates scalar fields and payload hash. Registry compatibility remains required before future business delivery. Logging-only history is not replayed automatically.

## Verification

Required SDK 10.0.301; real PostgreSQL tests used the committed PostgreSQL 18.6 image/digest and non-owner runtime roles. Diagnostic builds/restores retain NuGet audit with `-p:WarningsNotAsErrors=NU1902%3BNU1903`; no audit suppression, dependency upgrade or lock-file change was made.

| Check | Observed result |
| --- | --- |
| Unchanged-source PostgreSQL baseline | 38 directly affected cases passed, zero skips. |
| New E2 executor cases | 20 passed, zero skips: revoked user, durable event provenance, current recipient policy, actor-first membership scope, raw RLS/cross-org denial, pooled scope cleanup, atomic rollback/cancellation, competition, stale leases, backoff and expected-operation mismatch. Includes final-attempt lost-ack recovery, removed-module/grant recovery, exhausted work without effects and repeated pauses followed by availability. |
| New/affected unit cases | 82 passed, zero skips: registry, finite grants, explicit principal, outbox v1/v2, operational profiles, contract/manifest compatibility, delivery/recovery and audit behavior. |
| Affected integration regressions plus E2 | 96 passed, zero skips: E2, PostgreSQL inspection/RLS, organization HTTP middleware, outbox forward migration/recovery, audit atomicity and runtime role fixtures. |
| Debug solution build / OpenAPI generation | Passed with existing dependency warnings visible; no OpenAPI/client/registry drift. |
| Module doctor | Healthy. |
| Host and Projects/Documents/Federation architecture | 12 + 5 + 5 + 5 passed, zero skips. |
| Packed renamed React and backend-only outputs | Pack/install/generation passed; each output passed 39 focused unit and 20 E2 PostgreSQL cases, zero skips. This also executes the final timestamped migrations after namespace substitution. |
| Integrated review | Two recovery/budget findings fixed and verified in the same review; no unresolved actionable findings. |
| Standard locked restore | Failed on existing Pkcs 10.0.0 NU1902/NU1903 and Xml 10.0.11 NU1903 advisories. The PR remains draft and does not establish release readiness. |

No frontend behavior or public HTTP endpoint changed; browser/UI suites were not run. No deployed database, external email or scheduler was exercised. Fresh origin/develop was confirmed unchanged before publication. E3 fan-out, E4 scheduling/administration and E5 queued email remain planned.
