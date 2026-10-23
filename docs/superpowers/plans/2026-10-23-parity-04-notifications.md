# TryKatch Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task, preserving the project's ownership and single review rules.

**Goal:** Deliver a durable per-user organization inbox, reusable notification bell and optional approved-template emails.
**Architecture:** Optional five-layer Notifications module consumes explicitly registered integration events. Durable rows are authoritative; realtime invalidates/refetches recipient-scoped views.
**Tech Stack:** Existing module stack plus E3 consumers, E5 email and S2 realtime contracts.
**Spec:** [Design](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths are relative to `templates/trykatch/`; new names are proposed. Notifications has no required business-module dependency. Chat/Billing/Tickets adapters register only when the publishing module is installed; do not make the inbox depend on Chat. Default self-service is limited to the authenticated recipient.

## Review Focus

- Integration redelivery creates one row per logical recipient, not one per attempt.
- Current recipient membership and source-resource visibility govern disclosures and deep links.
- A bell extension cannot hardcode a new feature list in the shell.
- Mark-read/all-read never mutate another user's rows and preserve natural idempotency.
- Missed/duplicated pushes reconcile against durable inbox/cursor state.

## N1: Implement inbox data, recipient policy and consumers

**Files:** Create `src/Modules/Notifications/trykatch.module.json`; five `Trykatch.Modules.Notifications.{Domain,Application,IntegrationEvents,Presentation,Infrastructure}` projects under that directory; focused files `Domain/Notification.cs`, `Application/NotificationUseCases.cs`, `Presentation/NotificationsEndpoints.cs`, `Infrastructure/NotificationsModule.cs`, `NotificationsModelContributor.cs`, `NotificationEventConsumers.cs`. Add module-local unit/architecture projects under `tests/Modules/Notifications/` and host `tests/Trykatch.IntegrationTests/NotificationIntegrationTests.cs`. Register through `trykatch.modules.json`, solution/project graphs and generated registries.

**Interfaces:** Produce stable operation IDs `Notifications_List`, `Notifications_UnreadCount`, `Notifications_MarkRead`, `Notifications_MarkAllRead`; denormalized row includes organization, recipient, source event/message ID, source resource reference, safe title/body, created/read time and cursor. E3 consumer keys remain stable. Event subscriptions mean developer-registered consumers, not recipient-configurable preferences. No public API accepts an arbitrary recipient for inbox administration.

- [ ] Add `RepeatedEventCreatesOneNotificationPerRecipient`, `ActorCanReadOnlyOwnInbox`, `MarkReadTwicePreservesOriginalReadTime`, `MarkAllReadDoesNotTouchOtherRecipients` and `SourceRevocationSuppressesPrivateDetails` with real HTTP/RLS assertions in the integration project.
- [ ] Persist source-event/recipient deduplication with the inbox effect. Build safe deep links from registered route/resource metadata, not untrusted URLs or raw HTML. Re-evaluate current source visibility; deleted/disabled sources degrade to a safe unavailable message without disclosing prior private details.
- [ ] Add explicit adapters for installed event contracts. A committed-event consumer uses its bounded event grant; origin actor remains provenance. Resolve current eligible recipients and organization membership before storing/sending details.
- [ ] Persist realtime/email delivery intents atomically with the notification row and consumer inbox; dispatch only after commit. Add `CrashAfterNotificationCommitStillDispatchesPendingIntents`: restart recovers the pending sends without duplicate inbox rows. Email absent produces inbox-only behavior; a realtime outage leaves the count/list correct.
- [ ] Verify module unit/architecture tests, `NotificationIntegrationTests`, relevant generic module isolation/activation checks and common exit. Record the exact permission/self-service policy in the module manifest/spec; no broad admin inbox access is added implicitly.

## N2: Add shell contribution, inbox page and complete browser flow

**Files:** Create `src/Modules/Notifications/Web/` with package, module entry, `NotificationBell.tsx`, `NotificationInboxPage.tsx`, tests/stories and module-owned English/French messages. Add a published `shell.header.actions` extension point through `web/packages/module-sdk/src/index.ts`, `extensions.tsx` and `web/apps/web/src/shell/AppShell.tsx`; preserve app-owned overrides. Add `web/apps/web/e2e/notifications.spec.ts`; update module docs.

**Interfaces:** Bell contributes through the named slot and consumes generated endpoints plus S2 invalidation. The inbox route is `/notifications`; current user/organization changes reset unread state and subscriptions.

- [ ] Test empty/loading/error/inbox/unread/read states, count updates, mark-all and permission/session expiry. Assert one registered contribution and no orphan shell code when the module is disabled.
- [ ] Use cursor pagination and server-provided safe links; avoid per-row cross-module client queries. Reconcile after reconnect and prevent delayed responses from a previous organization replacing the current inbox.
- [ ] Browser acceptance: trigger a real installed-module event, observe one inbox row/badge update, mark read, reconnect and confirm durable state; a different recipient and organization see neither row nor push.
- [ ] Verify focused Notifications Web tests/stories, `playwright test e2e/notifications.spec.ts`, module SDK slot regressions, OpenAPI/client generation, typecheck/build and common exit.
- [ ] Record evidence in proposed `docs/specs/evidence/parity-notifications.md`; include the no-Chat, no-email and disabled-module combinations.
