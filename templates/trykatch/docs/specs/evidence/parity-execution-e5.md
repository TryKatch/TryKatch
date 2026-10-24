# E5 queued email implementation evidence

Status: In progress. Only the transport and protection building blocks below have been implemented and checked. Durable delivery acceptance, policy preparation, caller migration and operational surfaces are not yet implemented.

## Baseline and scope

Work began in a fresh isolated worktree from `origin/develop` at `b932b087a9dfe57d286f4cb4c9aeb75a1d06b2d1`, the merged E4 PR #131 tree. The full parity roadmap and Phase 1 exits remain required. See the [E5 specification](../queued-email.md).

Strict locked restore of the integration project and its references passed. The initial `SmtpTransportTests` baseline passed 17 cases with zero skips. No dependency, live database, external email provider or deployed application was changed.

## Transport checkpoint

The internal SMTP transport now returns an allowlisted disposition, stage, failure code and optional numeric SMTP status. It distinguishes acknowledged, retryable, permanent and uncertain outcomes without returning remote replies, message bodies, addresses, credentials or original exceptions. Existing `IEmailSender` remains synchronous and exposes the existing sanitized `EmailDeliveryException` for unsuccessful results. The existing invitation and recovery notifier contracts remain unchanged.

Real loopback SMTP checks verify transient/permanent recipient rejection, lost final acknowledgement, cancellation before transmission, acknowledgement during connection reset, and bounded cleanup cancellation. Existing TLS, authentication and production configuration checks remain selected. Missing STARTTLS support is permanent rather than a blind retry. Successful `SendAsync` is acknowledged before disconnect cleanup, and cleanup honors the caller's total attempt cancellation budget.

The initial dropped-QUIT experiment did not reproduce a legacy failure with MailKit 4.16.0; it is not evidence of an existing cleanup bug. A deterministic held-QUIT test waits until the server sees QUIT, then cancels the caller and requires prompt acknowledged completion. Deliberately replacing the cleanup cancellation token with `CancellationToken.None` made this test fail with `TimeoutException`; restoring the token passed. The reset fixture also waits for QUIT rather than sleeping after DATA acknowledgement.

The final targeted Release SMTP filter passed **23 cases, zero failures/skips**. After the deliberate regression and restoration, the single cleanup-budget case passed again. SMTP was confined to the loopback fixture; no real external email was sent.

## Protection checkpoint

Application defines a provider-neutral protected-payload seam and immutable protection context. The Infrastructure adapter uses the existing host Data Protection provider with purposes bound to format version, organization/Identity boundary, owner ID, delivery ID and template/version. Invalid or empty ownership context is rejected.

Three focused tests verify round-trip content, absence of plaintext recipient/token in the protected string, and cryptographic rejection when changing boundary, owner, delivery or template/version. The initial test failed to compile because the seam did not exist; the implemented adapter then passed. The combined Release protection and existing invitation-template filter passed **15 cases, zero failures/skips**: three new protection cases and 12 existing invitation-template compatibility cases. This verifies the primitive; actual queue-row protection, key rotation across delivery processes and final DI composition remain pending.

## Remaining E5 acceptance

The durable organization/Identity stores, forward migrations and exact grants/inspection; uncertain enqueue/claim/completion recovery; current source/recipient/token checks on every attempt; bounded dispatcher/retries; truthful invitation queue reporting and generic public recovery integration; safe operator listing/previews; optional email/backend-only generation; integrated review and complete E5 qualification are still required. No E5 completion, release readiness or queue delivery guarantee is claimed from this checkpoint.
