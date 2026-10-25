# S5 current-organization usage inspection

This is component evidence for Task8, not S5 merge or release acceptance. The freshly packed products, composed RustFS/two-process replicas and generated-application browser journey remain required.

## Implemented boundary

`Quotas_CurrentUsage` exposes `GET /api/v1/quotas/usage` to a current human membership with either `organizations.read` or `organizations.manage`. A fixed endpoint policy rejects unauthorized requests before organization request counting; the application reader independently verifies the actual enlisted ReadCommitted transaction, organization/actor GUC frame, supported active placement and fresh membership/permission. The technical maintenance principal receives no fabricated membership or inspection permission.

The response contains installed/retained public resource identity and display name, unit, allowance mode/version/source, installed state and aggregate quantities. Limits and all quantities are decimal Int64 strings. Object keys, actor/effect identities, inventory cursors and editable allowances are absent. Projection, inventory and aggregates share one SQL statement snapshot. Expired reservations and already-fencing uploads remain reconciliation-held; neither is refunded by inspection. Overflow becomes unavailable without truncating values or aborting the caller transaction.

Incomplete inventory leaves committed usage unknown. An installed source queues one closed inventory reference in the caller transaction, including with disabled limits. The worker owns scanning and completion. Rollback removes that intent; repeated reads cannot create duplicate live work. A declared source requires exact source-version completion. Legacy declarations without a source retain their explicitly completed checkpoint, as specified by the accepted inventory compatibility ruling. An incomplete checkpoint without a source pauses inspection and blocks finite admission until its source is restored and reconciliation completes. A retained absent owner is paused and preserves known usage/history. An older policy node cannot invent a missing projection. Publication outage returns sanitized 503.

The forward `RetainQuotaResourceDisplayNames` migration adds only nullable display metadata to the existing entitlement relation. The publisher retains current names after module removal and refreshes legacy metadata under the existing sorted locks, without changing allowance versions or history. Previously absent legacy resources with no recorded name honestly use the public resource identity as their label. No role, permission, module-private table, allowance-editing API or assistant tool is added.

Settings now includes an accessible Usage tab. AI activation/model/key drafts survive tab switches. The panel queries only when authorized and active, uses generated transport types and BigInt arithmetic, shows known aggregates in every mode and preserves unknown totals. Stories cover loading/retry, empty/permission, zero/finite/Unlimited/over-limit, initializing/paused/unavailable and retained resources. Limits disabled means tracking continues. The server state remains authoritative for an unavailable retained resource.

## Verification

All successful selected cases below ran with zero skips; their TRX/logs are in the ignored S5 evidence workspace. Successful evidence is reused until a source change invalidates it.

| Evidence | Result |
| --- | --- |
| `quota-usage-name-publication-green.trx` | 36 real PostgreSQL/API cases: 12 reader, 5 HTTP and 19 directly affected publication cases |
| `quota-usage-supplement-profiles-green.trx` | 5 cases: pre-expiry fencing classification, caller rollback/cancellation, legacy name refresh and 2 existing exact-profile/inspection cases |
| `quota-usage-http-outage-green.trx` | 1 actual HTTP publication outage, sanitized 503 without capacity |
| `quota-usage-missing-projection-green.trx` | 1 older-node/missing-projection case |
| PostgreSQL/API union | 43 distinct cases: 16 new reader + 6 new HTTP + 21 affected publication/profile cases |
| `quota-absent-source-legacy-retry.trx` | 22 actual PostgreSQL cases: 3 additional incomplete-source inspection/admission/restoration cases, 18 affected system/boundary cases and the explicit legacy completed-checkpoint compatibility case |
| `quota-usage-composition-schema.trx` | 16 composition and schema/profile units |
| `quota-usage-architecture.trx` | 2 module reference/scanning architecture cases |
| `task8-ui-final-green.log` | 22 affected Settings behavioral tests, including model/key/activation draft preservation |
| `task8-settings-stories.log` | 25 real component interaction/accessibility stories |
| Normal API/OpenAPI build and client generation | Successful; stable operation and explicit string quantities emitted through the normal generator |
| Web/API-client typechecks and web production build | Successful; existing large-chunk advisory remains, with no build error |
| Fixture exclusion, assistant contract check, module doctor | Successful; normal catalog healthy |

Actual browser layout inspection used 320/768/1440 pixels, English/light and French/dark. The document width matched each viewport. Phone amounts use full-width stacked rows so exact large digits remain readable. Screenshots are under the primary checkout's ignored `.playwright-mcp/task8-usage-*` paths. These rendered stories use sample data; they are not the required generated-application backend journey.

## Failures investigated and remaining gates

The missing reader first fails compilation. Its unavailable scaffold then fails all 10 selected cases. The first implementation passes 9; the remaining fixture inserted usage before its restricting entitlement FK existed. Publishing the default projection before that seed corrects the fixture; 10 pass. Four HTTP cases then fail on the absent route before the endpoint is implemented. Two additional semantic failures expose a denied request consuming a throttle permit and the technical principal throwing for membership; endpoint OR authorization and explicit technical denial correct them. The display-name regression exposes the lost readable label after owner removal; the nullable forward metadata migration and publisher preservation correct it.

The first HTTP test file needed its JSON extension import, nullable assertion and static expected-field array before semantic execution. The first UI tab tests expose the missing tab. A story object syntax error is corrected before typecheck succeeds. The initial Storybook invocation used the wrong relative filter; the corrected invocation runs 24 cases. Two viewport assertions incorrectly assume toolbar globals resize the Vitest browser. Stories now use the correct viewport global shape and actual browser resizing provides layout evidence. Early oversized/nonproduction story fixtures were corrected to valid Int64 values, GUID versions and the real host source before final evidence. Access loading/failure now remains distinct from denial. A further cached-permission regression first requests usage after access revalidation fails; disabling the query for access loading/error corrects it. Its first test needed a panel-scoped text assertion because both mounted panels retain the same error text. Final Settings behavior is 22/22 and stories 25/25. No security rule or business requirement was weakened.

Source-removal investigation initially assumed even a completed legacy checkpoint should become unavailable without its declaration. That expectation contradicted the accepted compatibility ruling and existing explicit regression. Temporary stricter predicates were reverted; no production source-removal change is claimed. The added cases instead verify incomplete inventory without a source, restoration through the real inventory worker and both finite admission gates. The first final selection failed entirely during Testcontainers resource-reaper initialization, before assertions. Its terminal rerun passes all 22 cases with zero skips.

The first newly packed Horizon product runs all 47 selected quota PostgreSQL/storage cases: 45 pass and two fail on test-only expectations that template renaming changes the Documents event wire name. The product correctly preserves its existing compatibility name. The affected SQL assertions now preserve that literal during template expansion. All six directly affected canonical upload/process/legacy cases pass with zero skips (`quota-wire-compatibility.trx` and `quota-wire-compatibility-legacy.trx`). This is not yet a successful renamed-product qualification. Requested packed-product artifacts are now copied before verifier/cleanup even when the actual test command fails, while its original exit code remains a failure.

Task8 remains open for fresh renamed React/backend-only normal and locked restores, Release builds/doctor, contract parity and nonzero PostgreSQL/storage execution; separate operating-system API replicas with RustFS, last-slot/failed/expired uploads, exact bytes and durable usage/throttling; actual generated-app Usage UI, restart and absent-source/re-enable retention; one whole-S5 review, final-head CI and guarded merge. The earlier transient multipart-antiforgery 500 remains a final-validation observation, with no causal fix claimed. S6, phases3–11 and final main release remain required.
