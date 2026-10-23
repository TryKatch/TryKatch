# TryKatch Webhooks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task; preserve the project's single review workflow.

**Goal:** Give each organization controlled, signed, durable outbound event subscriptions and delivery diagnostics.
**Architecture:** Optional five-layer module consumes only explicitly exported safe event DTOs. It owns subscriptions/delivery intents; host jobs perform protected network delivery outside the domain transaction.
**Tech Stack:** Existing module stack, Data Protection, HttpClient resilience and E3/E4/S3 contracts.
**Spec:** [Design](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints; paths are relative to `templates/trykatch/` and new names are proposed. There is no required business-module dependency. Do not export arbitrary internal events, audit records, identity tokens or assistant tool catalogs. Subscription secrets are recoverable encrypted signing keys, not misleading one-way hashes.

## Review Focus

- SSRF protection survives DNS rebinding, redirects, retries and operator test/redelivery actions.
- Subscription changes cannot alter an already recorded delivery payload or silently disclose another organization's event.
- HMAC signs documented canonical bytes and a stable delivery identity.
- An uncertain HTTP acknowledgement can duplicate external delivery; stable IDs make that visible/deduplicable.
- Disablement, rotation and deletion preserve forensic history without continuing unauthorized sends.

## W1: Add subscription model and explicit export catalog

**Files:** Create `src/Modules/Webhooks/trykatch.module.json` and five Webhooks projects, with `Domain/WebhookSubscription.cs`, `Application/WebhookSubscriptionUseCases.cs`, `Presentation/WebhooksEndpoints.cs`, `Infrastructure/WebhooksModule.cs`, `WebhookModelContributor.cs`, `WebhookSecretProtector.cs`; shared `src/Common/Trykatch.Modules.Abstractions/Eventing/ExportedEventDescriptor.cs`. Add module tests and host `WebhookSubscriptionIntegrationTests.cs`.

**Interfaces:** Operation IDs `Webhooks_List`, `Webhooks_Create`, `Webhooks_Update`, `Webhooks_Disable`, `Webhooks_RotateSecret`, `Webhooks_ListEvents`. Permissions `webhooks.read`, sensitive `webhooks.manage`, `webhooks.test`, `webhooks.redeliver`; no new non-Owner default grants without explicit spec approval. Export descriptors name stable type/version, owner module, safe payload serializer and organization boundary.

- [ ] Test cross-organization subscription IDs, unknown/nonexported event names, disabled source modules, protected-secret serialization and unauthorized configuration. Assert list APIs never return the stored secret and wildcard selection, if supported, matches only the current explicit export catalog.
- [ ] Persist safe subscription configuration and encrypted secret/version. Reauthorize updates/rotation; reveal a newly generated secret only through an explicitly protected creation/rotation response. Preserve prior delivery key-version audit without exposing keys.
- [ ] Validate destination policy on create/update and again at delivery. Add versioned public event schema docs; sensitive internal events are not automatically selectable.
- [ ] Verify module unit/architecture tests and `WebhookSubscriptionIntegrationTests`, generic RLS/module-boundary cases and common exit.

## W2: Implement fan-out, protected delivery, retries and redelivery

**Files:** Add Webhooks Application `WebhookEventConsumer.cs`, `WebhookDeliveryUseCases.cs`, Infrastructure `WebhookDeliveryJob.cs`, `WebhookHttpTransport.cs`, `WebhookDestinationPolicy.cs`, Domain `WebhookDelivery.cs`/`WebhookAttempt.cs`, forward migrations and host tests `WebhookDeliveryIntegrationTests.cs`. Add unit `WebhookSigningTests.cs`, `WebhookDestinationPolicyTests.cs`.

**Interfaces:** Consume E3 safe export/context and E4 scheduler. Produce delivery ID, event version, organization, subscription/version, immutable safe payload hash/bytes and attempts. Registered job `webhooks.deliver.v1`. Sign a documented versioned envelope containing timestamp, stable delivery ID and exact body bytes with HMAC-SHA256; receiver sample verifies freshness and dedupes the stable ID.

- [ ] Test duplicate event fan-out, two workers, transient timeout/5xx, bounded retry exhaustion, terminal 4xx policy and explicit redelivery. Assert `logicalDeliveries == 1` per subscription/event, attempts preserve delivery identity, and receiver-side duplicates remain possible after an uncertain acknowledgement.
- [ ] Persist delivery intent and E3 inbox atomically, then send outside the transaction. Recheck subscription active state/current organization policy at each attempt. Rotation uses an explicit recorded signing-version policy; never silently sign with an incompatible key.
- [ ] Add deterministic tests for loopback/private/link-local/metadata destinations, IPv4/IPv6 encodings, DNS rebinding, credentials in URLs, redirects, oversized response and timeout. Resolve/validate the actual connected endpoint on every attempt with TLS hostname validation preserved. Default deny private destinations; exceptions require deployment-owned allowlists.
- [ ] Protect test delivery with exactly the same transport rules and permissions. Redelivery is audited, rate-limited and revalidated; sanitize response diagnostics and cap body storage. Never log signing keys or full sensitive event bodies.
- [ ] Verify `WebhookSigningTests`, `WebhookDestinationPolicyTests`, `WebhookDeliveryIntegrationTests`, relevant event/job tests and common exit. Use deterministic local/fake endpoints; no calls to customer systems.

## W3: Add configuration and delivery investigation UI

**Files:** Create Webhooks `Web/` package/entry with `WebhookSubscriptionsPage.tsx`, `WebhookDeliveryPage.tsx`, tests/stories/messages; add `web/apps/web/e2e/webhooks.spec.ts`, docs and receiver verification examples.

**Interfaces:** Generated client exposes permission-gated subscription configuration, explicit event picker, one-time secret handling and sanitized delivery/attempt pagination. Route contributions `/webhooks` and `/webhooks/deliveries` remain module-owned.

- [ ] Test create/edit/disable/rotate/test/redeliver, invalid destination, empty event catalog, failed delivery and permissions. Secret display is short-lived component state; never persist it to browser storage, telemetry or query cache.
- [ ] Browser acceptance creates an organization subscription to a fixture event, observes signed delivery/history, simulates retry and explicitly redelivers; another organization cannot inspect or trigger that delivery.
- [ ] Verify focused Web tests/stories, named browser flow, generated client, typecheck/build and common exit. Record evidence in proposed `docs/specs/evidence/parity-webhooks.md`.
