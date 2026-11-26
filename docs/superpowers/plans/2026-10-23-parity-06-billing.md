# TryKatch Billing and Organization Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task; preserve Sol/Luna routing and one review pass.

**Goal:** Add configurable subscription, invoice and usage primitives with organization lifecycle administration.
**Architecture:** A manually authored mixed-ownership Billing module owns platform plan management and organization financial records. A host-owned entitlement projection prevents kernel dependencies on Billing runtime code.
**Tech Stack:** Existing module stack, exact decimal/string API values, PostgreSQL transactions and E3/E4/E5/S3/S4/S5 contracts.
**Spec:** [Design, D2 and ownership](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply roadmap constraints; paths are relative to `templates/trykatch/`; new names are proposed. D2 gates issued financial documents and automatic expiry enforcement. No tax engine, payment gateway, recurring charge or accounting-compliance claim is included. Notifications/email are optional integrations.

## Review Focus

- Platform-editable plans cannot be implemented as writable `global-read-only` resources.
- Historical invoice prices and terms remain immutable after issuance and plan changes.
- Concurrent renewals/jobs/events produce one period invoice and one active subscription.
- Quota usage is counted once and retains exact values; billing is not calculated from volatile cache counters.
- Subscription expiry does not become platform-to-organization data bypass or automatic data destruction.

## B1: Add versioned plans, subscription terms and entitlement projection

**Files:** Manually create `src/Modules/Billing/trykatch.module.json` and five Billing projects; proposed Domain `BillingPlan.cs`, `PlanVersion.cs`, `Subscription.cs`, `BillingPeriod.cs`, Application `PlanUseCases.cs`, `SubscriptionUseCases.cs`, Infrastructure `BillingModule.cs`, separate platform/organization stores/model contributors and immutable migrations. Extend S5's host-only projection publisher. Add unit `SubscriptionTests.cs`, `EntitlementProjectionTests.cs`, host `BillingOwnershipIntegrationTests.cs`.

**Interfaces:** Platform operations `BillingPlans_List/Create/Update/Publish`; organization `Billing_GetSubscription`, `Billing_ChangeSubscription`, `Billing_SuspendSubscription`, `Billing_CancelSubscription`. Separate platform plan permissions and `billing.read`/sensitive `billing.manage`; host reads only approved effective entitlements, not Billing entities.

- [ ] Resolve D2's term/renewal/proration/currency/access policy in `docs/specs/billing.md`. Fixtures may use explicit synthetic prices/currencies; fixture values are not product defaults. Define Active/Suspended/Cancelled legal transitions and at-most-one-active constraint.
- [ ] Test `OrganizationRoleCannotWritePlans`, `PlatformRoleCannotReadOrganizationInvoices`, `PlanProjectionIsVersioned`, `PlanChangeDoesNotMutatePriorTerms`, competing active subscriptions and event-ordering of projection updates.
- [ ] Persist editable plans as platform-owned rows. Publish an immutable safe plan/entitlement projection through a reviewed host seam; avoid broad organization SELECT on platform data or platform CRUD on reference-global rows. Handle missing Billing with the host-configured default provider from S5.
- [ ] Emit stable `billing.subscription-changed.v1` contracts through IntegrationEvents and update projection idempotently/version-monotonically. Downgrade blocks future over-limit allocations according to explicit policy; it never purges existing records automatically.
- [ ] Verify Billing unit/architecture tests, `BillingOwnershipIntegrationTests`, affected quota/isolation/grant tests and common exit. Do not use the organization-only scaffold as proof of mixed-ownership correctness.

## B2: Add immutable invoice lifecycle and scheduled usage invoicing

**Files:** Billing Domain `Invoice.cs`, `InvoiceLine.cs`, `UsageSnapshot.cs`; Application `InvoiceUseCases.cs`, `UsageInvoiceGenerator.cs`; Infrastructure `MonthlyInvoiceJob.cs`, `BillingUsageConsumer.cs`; Presentation `InvoicesEndpoints.cs`; module unit `InvoiceTests.cs`, `UsageInvoiceTests.cs`, host `BillingInvoiceIntegrationTests.cs`.

**Interfaces:** `Billing_ListInvoices`, `Billing_GetInvoice`, `Billing_CreateDraftInvoice`, `Billing_IssueInvoice`, `Billing_MarkInvoicePaid`; invoice read/manage/settle permissions are distinct where authority differs. Job `billing.generate-period-invoices.v1`; unique logical key `(OrganizationId, SubscriptionId, PeriodStartUtc, InvoiceKind)` plus unique organization display number. Period boundaries are `[start, end)` under D2.

- [ ] Test Draft→Issued→Paid, rejection of illegal transitions, exact totals/currency rounding from D2, missing commercial policy, duplicate usage events and concurrent/repeated monthly jobs. Assert `invoicesForPeriod == 1` and issued lines/totals never change after price edits or repeated delivery.
- [ ] Build draft lines from immutable term and usage snapshots; issue atomically with required audit/outbox/idempotency. Mark-paid is an explicit authorized manual/domain action, not a claim that money was collected. Record settlement provenance and reject a second conflicting settlement.
- [ ] Schedule recurring generation through E4; catch up missed periods deterministically after downtime, with operator-visible boundaries. Do not invoice an unclosed/undefined period or silently convert currencies. Usage corrections after issuance create explicit subsequent adjustment intent governed by D2, not edits to the old invoice.
- [ ] Publish safe invoice/subscription events for Notifications/email/Webhooks. Protect recipient billing information and render invoices from immutable records, not a live plan lookup. PDF export may use the existing optional exporter adapter, with explicit unavailable behavior when absent.
- [ ] Verify module invoice/usage tests, `BillingInvoiceIntegrationTests`, affected S3/S5/E3 tests and common exit. D2-unresolved acceptance remains explicitly blocked rather than claimed complete.

## B3: Add lifecycle guards, operator/customer UI and recovery

**Files:** Host organization control seam in `src/Common/Trykatch.Application/Organizations/`, Infrastructure lifecycle projection/guards, Billing `Web/` pages `BillingPlansPage.tsx`, `SubscriptionPage.tsx`, `InvoicesPage.tsx`, tests/stories/messages; optional Notifications/email adapters; `web/apps/web/e2e/billing.spec.ts`.

**Interfaces:** Versioned lifecycle projection exposes active/suspended/expiry/grace state without importing Billing implementation into kernel middleware. Host guards explicitly exempt only D2-approved recovery/billing/auth endpoints and apply equally to HTTP, jobs and realtime subscriptions.

- [ ] Test renewal idempotency, expire/grace boundaries with controlled clock, suspend/reactivate, missing projection version and concurrent renewal/expiry work. An expired actor cannot bypass a guard through a background job or alternate module route.
- [ ] Provide platform plan administration and organization subscription/invoice views through their correct shells. Clearly distinguish plan entitlement, feature flag and individual permission; current application branding remains the fallback.
- [ ] Browser acceptance publishes a synthetic plan, assigns/renews a subscription, issues a fixture invoice and records explicit settlement; test an unauthorized actor, replayed submit and expiry-policy recovery. No paid gateway or live customer charge is involved.
- [ ] Add scheduled expiry scan, queued approved emails, operational dashboards and retry/reconciliation documentation. Validate retained history under disablement; explain how the host-configured entitlement provider is restored if Billing is removed.
- [ ] Verify lifecycle integration filter `FullyQualifiedName~BillingLifecycleIntegrationTests` (new host test), named browser test, focused component tests, generation/typecheck/build and common exit. Record evidence in proposed `docs/specs/evidence/parity-billing.md`.
