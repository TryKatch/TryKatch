# E5 queued email implementation evidence

Status: In progress. The transport, protection, delivery policy, owned schemas and Identity lease mechanics below have been implemented and checked. Durable delivery acceptance, current-policy preparation, dispatch, caller migration and operational surfaces are not yet implemented.

## Baseline and scope

Work began in a fresh isolated worktree from `origin/develop` at `b932b087a9dfe57d286f4cb4c9aeb75a1d06b2d1`, the merged E4 PR #131 tree. The full parity roadmap and Phase 1 exits remain required. See the [E5 specification](../queued-email.md).

Strict locked restore of the integration project and its references passed. The initial `SmtpTransportTests` baseline passed 17 cases with zero skips. No dependency, live database, external email provider or deployed application was changed.

## Transport checkpoint

The internal SMTP transport now returns an allowlisted disposition, stage, failure code and optional numeric SMTP status. It distinguishes acknowledged, retryable, permanent and uncertain outcomes without returning remote replies, message bodies, addresses, credentials or original exceptions. Existing `IEmailSender` remains synchronous and exposes the existing sanitized `EmailDeliveryException` for unsuccessful results. The existing invitation and recovery notifier contracts remain unchanged.

Real loopback SMTP checks verify transient/permanent recipient rejection, lost final acknowledgement, cancellation before transmission, acknowledgement during connection reset, and bounded cleanup cancellation. Existing TLS, authentication and production configuration checks remain selected. Missing STARTTLS support is permanent rather than a blind retry. Successful `SendAsync` is acknowledged before disconnect cleanup, and cleanup honors the caller's total attempt cancellation budget.

The initial dropped-QUIT experiment did not reproduce a legacy failure with the locked MailKit 4.17.0; it is not evidence of an existing cleanup bug. A deterministic held-QUIT test waits until the server sees QUIT, then cancels the caller and requires prompt acknowledged completion. Deliberately replacing the cleanup cancellation token with `CancellationToken.None` made this test fail with `TimeoutException`; restoring the token passed. The reset fixture also waits for QUIT rather than sleeping after DATA acknowledgement.

The final targeted Release SMTP filter passed **23 cases, zero failures/skips**. After the deliberate regression and restoration, the single cleanup-budget case passed again. SMTP was confined to the loopback fixture; no real external email was sent.

## Protection checkpoint

Application defines a provider-neutral protected-payload seam and immutable protection context. The Infrastructure adapter uses the existing host Data Protection provider with purposes bound to format version, organization/Identity boundary, owner ID, delivery ID and template/version. Invalid or empty ownership context is rejected.

Three focused tests verify round-trip content, absence of plaintext recipient/token in the protected string, and cryptographic rejection when changing boundary, owner, delivery or template/version. The initial test failed to compile because the seam did not exist; the implemented adapter then passed. The combined Release protection and existing invitation-template filter passed **15 cases, zero failures/skips**: three new protection cases and 12 existing invitation-template compatibility cases. This verifies the primitive; actual queue-row protection, key rotation across delivery processes and final DI composition remain pending.

## Owned schema and lease checkpoint

Application now defines transport-neutral lease, preparation and completion contracts. One bounded policy permits five attempts, exponential retry delays starting at five seconds, a two-minute lease and a 45-second whole-attempt budget. Contradictory acknowledgement/status combinations are rejected; default diagnostic text for prepared messages, protected leases and reset intents omits private content. The focused policy/protection/access-profile filter passed **14 cases, zero failures/skips**, after the missing ownership declarations, missing organization mapping and inconsistent SMTP status tests failed first.

The additive Identity and Application migrations create `identity.email_deliveries` and `infrastructure.email_deliveries`. Both contain bounded operational metadata and protected-intent storage, with no recipient/token plaintext columns, foreign-key cascade or deletion policy. Mapping and schema declarations remain outside optional SMTP code so disabling email retains migration/history inspection. Identity receives SELECT/INSERT/UPDATE only; organization runtime receives scoped INSERT only on its relation; the coordinator receives SELECT/UPDATE only on that organization relation. Other runtime roles receive no access. Both allocations are reserved before generic schema grants. Organization delivery uses ENABLE/FORCE RLS with exact organization/actor append and coordinator policies.

The actual migrator passed on a fresh database, on a second idempotent invocation and on a database first migrated through E4. Existing Identity data survived the forward upgrade. Tests exercise all five role profiles, actual Identity lease operations under the narrow Identity runtime role, missing/mismatched organization and actor scope, and transaction rollback. Application, Platform and Identity snapshots match their current production models without opening a database. The affected inspection/migration run passed 34 of 35 cases; its sole failure was an existing custom-Identity-role fixture missing the new exact queue grant. That fixture was corrected without weakening inspection and the failed case passed separately. Together with the separately selected snapshot case, **36 distinct migration/inspection cases pass, zero skips**.

Identity queue mechanics use a dedicated context without an EF retry strategy. Each claim commits before exposing a lease; an uncertain commit can recover only the same assigned lease IDs, in a fresh context after disposal of the original transaction, with a five-second recovery budget. Completion uses delivery/owner/template/attempt/lease compare-and-set and retains the completed lease outcome for idempotent acknowledgement. Expired exhausted leases are terminalized in an ordered, limited `SKIP LOCKED` batch. Focused real-PostgreSQL mechanics passed **five cases, zero failures/skips**. Regressions first demonstrated unbounded cleanup and blocking on a locked exhausted row; both pass after correction. A post-commit transaction interceptor verifies exact committed-lease recovery. Those mechanics fixtures use disposable owner-backed `EnsureCreated`; the separate actual-migrator/runtime-role test supplies production schema/grant evidence. They do not establish token validation, protected queue admission, or an end-to-end SMTP guarantee.

Scaffolding used the pinned EF tool and an ignored design-only host; its final restore imports the application's central security pins and passes without advisory exceptions. No dependency lock, deployed database or external provider changed. This remains an implementation checkpoint, without the final integrated E5 review or PR qualification.

## Remaining E5 acceptance

Organization queue operations and approved template/disclosure registration; immutable admission and uncertain enqueue/completion recovery; current source/recipient/token checks on every attempt; bounded dispatcher; truthful invitation queue reporting and generic public recovery integration; safe operator listing/previews; optional email/backend-only generation; integrated review and complete E5 qualification are still required. No E5 completion, release readiness or queue delivery guarantee is claimed from this checkpoint.
