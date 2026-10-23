# TryKatch functional parity design

**Status:** Proposed implementation scope; no implementation or policy approval is implied.
**Observed:** 2026-10-23, `origin/develop` at `d3e8dc40b6c5d4d20a173ec2f884353f4f25a7d9`.
**Requested outcome:** Match the application capabilities discussed in the FullStackHero comparison while preserving TryKatch's architecture.
**Delivery plan:** [Phased roadmap](../plans/2026-10-23-feature-parity-plan.md).

## 1. Outcome, scope and assumptions

Developers can generate a TryKatch application, select optional capabilities, and deliver organization-based software with notifications, integrations, subscriptions, product data, support workflows and messaging. Operators can administer those capabilities without gaining implicit access to organization data.

The user supplied two requirements: plan the additions discussed in this conversation in detail, and preserve TryKatch's architecture. The decomposition, technical defaults and delivery order below are proposed engineering choices, not previously approved product decisions.

Functional parity means comparable observable workflows. It does not mean the same project count, libraries, endpoint names, browser token storage or physical database topology. FullStackHero documentation is the comparison baseline; this work has not executed its application or certified either project's production readiness.

Included work:

- Six optional full-stack modules: Notifications, Webhooks, Billing, Catalog, Tickets and Chat.
- Extensions to Documents, identity/session administration, organization settings, auditing and email.
- Shared durable execution, event consumers, scheduled jobs, realtime delivery, distributed caching, endpoint idempotency, runtime feature evaluation, entitlements and usage quotas.
- Default/backend-only/optional-module packaging, frontend workflows, documentation and operational qualification for the additions.

Separate decisions or later scope:

- Direct-to-storage presigned uploads/downloads, dedicated PostgreSQL placement, and provider-specific deployment recipes.
- Payment collection/gateways, tax calculation, accounting compliance, SLA/escalation/email-to-ticket workflows, mobile clients and a public module marketplace.
- SQL Server support, separate admin/dashboard applications, runtime loading of arbitrary modules, microservices and replacement of cookie/BFF authentication.
- New state-changing assistant tools. Existing read-only assistant behavior stays deny-by-default.

## 2. Verified starting point

| Area | Existing evidence | Consequence for this plan |
| --- | --- | --- |
| Module composition | [Module guide](../../../templates/trykatch/docs/modules.md), [ADR 0011](../../../templates/trykatch/docs/adr/0011-build-time-modular-monolith.md) | Use explicit catalog, five backend layers and optional Web; no private cross-module references. |
| Ownership and grants | [Ownership rules](../../../templates/trykatch/src/Common/Trykatch.Modules.Abstractions/TrykatchDataResourceRules.cs), [runtime profiles](../../../templates/trykatch/src/Common/Trykatch.Infrastructure/Organizations/RuntimeDatabaseAccessProfiles.cs) | New infrastructure tables and mixed-ownership Billing require explicit grant design. `host-only` is not an automatic grant. |
| Organization transactions | [Organization context](../../../templates/trykatch/src/Common/Trykatch.Application/Organizations/IOrganizationContext.cs), [transaction middleware](../../../templates/trykatch/src/API/Trykatch.Api/Security/OrganizationTransactionMiddleware.cs) | Async work must establish its own validated host execution boundary. |
| Outbox | [ADR 0010](../../../templates/trykatch/docs/adr/0010-transactional-outbox.md), [envelope](../../../templates/trykatch/src/Common/Trykatch.Application/Outbox/IOutboxWriter.cs), [default transport](../../../templates/trykatch/src/Common/Trykatch.Infrastructure/Persistence/LoggingOutboxTransport.cs) | Durable storage/replay exist; default delivery logs only. Add consumers without changing historical message identities. |
| Documents | [README](../../../templates/trykatch/src/Modules/Documents/README.md) | Extend existing private storage and streamed transfers; do not create a duplicate Files module. |
| Identity | [MFA guide](../../../templates/trykatch/docs/mfa-step-up.md), [identity composition](../../../templates/trykatch/src/Common/Trykatch.Identity/DependencyInjection.cs) | Preserve MFA and recent assurance; add persistent session administration, groups and optional advanced policies. |
| Provisioning | [Creation use case](../../../templates/trykatch/src/Common/Trykatch.Application/Organizations/CreateOrganization.cs), [database guide](../../../templates/trykatch/docs/database.md) | Shared placement and retry identity exist. Dedicated placement is explicitly rejected by the current application flow. |
| Email and branding | [Branding guide](../../../templates/trykatch/docs/branding.md), [infrastructure composition](../../../templates/trykatch/src/Common/Trykatch.Infrastructure/DependencyInjection.cs) | SMTP is optional; current branding is application/operator-wide. Add durable delivery and organization-scoped settings. |
| Qualification | [Verification guide](../../../templates/trykatch/docs/development/verification.md), [readiness plan](../../production-readiness-plan.md) | Existing unfinished release gates remain unfinished; passing a feature test does not close them. |

Older plans can describe desired features that are not shipped. Current source and executable qualification take precedence when establishing the baseline.

## 3. Architecture constraints

1. Preserve .NET 10, PostgreSQL, React/TanStack, Aspire and OpenTelemetry. The current SDK is `10.0.301` with `latestPatch`; retain the `10.0.3xx` feature band. Node.js is `24+`; pnpm is `10.17.1`. Do not bundle unrelated dependency upgrades.
2. Identity/session issuance, organization resolution, authorization, forced RLS, antiforgery, audit, outbox and module validation remain host-owned security controls.
3. Every organization-owned row implements `IOrganizationOwned`; every relation has declared ownership, tenant-first indexing, validated migrations and the correct live PostgreSQL policy. Sensitive relationships include organization identity so cross-organization links cannot be forged.
4. Each business module retains Domain, Application, IntegrationEvents, Presentation and Infrastructure projects. Infrastructure is its host composition entry. Web is optional.
5. Cross-module project references target IntegrationEvents only. Add shared neutral host interfaces when an explicit query/attachment/entitlement capability is required; never reference another module's Application or Infrastructure project.
6. `trykatch.modules.json` remains the single application-owned composition source. Generate API/migrator/Web registries and the lockfile; do not hand-edit them or scan assemblies dynamically.
7. Browser authentication stays same-origin HttpOnly cookies plus antiforgery. New realtime endpoints follow the same identity model. Platform authority does not imply organization membership.
8. Domain writes, required audit records and integration-event enqueueing commit atomically. External SMTP/HTTP/storage effects require durable delivery state and explicit retry/compensation; do not claim exactly-once delivery.
9. Applied migrations are immutable and forward-only. Disabling/unregistering a module retains data and history. Purge is a separate explicit retention operation.
10. Stable operation IDs and versioned event contracts are required. Decimal and 64-bit integer API values use invariant JSON strings; timestamps are UTC.
11. Reuse Storybook, shared UI, floating controls, English/French catalogs and permission-driven navigation. Generated clients and assistant contracts remain machine-owned.
12. Run the smallest relevant checks during implementation. Broaden only for a cross-cutting release qualification or a demonstrated unresolved concern, explaining why.
13. Optional modules are opt-in in generated products. Installing/enabling code is a build-time action; flags and plan settings cannot activate missing code or grant permissions.
14. Template/application namespace replacement and backend-only output must continue working. Every introduced host dependency remains behind a cohesive interface and an optional adapter where appropriate.

## 4. Proposed contracts and ownership

All names in this section are proposed. Phase 0 freezes them before implementation and records changes in one contract ledger. Methods exposed to modules belong in `Trykatch.Modules.Abstractions`; HTTP-specific contributions belong in `Trykatch.Modules.AspNetCore`. Host implementation stays in the existing owning Application/Infrastructure/API projects.

| Contract | Responsibility and fixed behavior |
| --- | --- |
| `ModuleExecutionRequest` / `IOrganizationWorkExecutor.ExecuteAsync(request, ct)` | Identify a registered module operation, organization, origin actor or system grant, work identifier and version. Host resolves authority/placement and owns scope/transaction. The durable payload cannot supply arbitrary permissions, connection strings or executable type names. |
| `IntegrationEventDescriptor` / `IModuleEventConsumer.HandleAsync(context, payload, ct)` | Explicit event type/version and stable consumer key; validate registry uniqueness and trusted publisher scope. Context contains persisted host-derived organization, actor, message ID and trace metadata. |
| `IModuleJobScheduler.EnqueueAsync(request, ct)` / `ScheduleAsync(request, dueAtUtc, ct)` | Register versioned job names, schedule durable work and return a work ID. Recurring definitions are idempotent, UTC-based and module-owned. Capture work intent, never cookies or permission snapshots. |
| `IModuleRealtimePublisher.PublishAsync(notification, ct)` | Publish a small typed invalidation/notification after durable commit to a host-authorized organization/user/channel audience. Never authorize by a client-supplied group string. |
| `IOrganizationCache.GetOrCreateAsync(key, factory, ct)` / `InvalidateAsync(key, ct)` | Automatically partition by environment, application and organization; add actor scope for user-specific values. Cache DTOs, not live EF entities or authority grants. |
| `IOrganizationFeatureEvaluator.IsEnabledAsync(featureKey, ct)` | Evaluate a declared installed feature for current environment/organization. Disabled evaluation cannot be bypassed with a direct API request. |
| `IOrganizationEntitlementReader.GetAsync(resourceKey, ct)` | Read effective resource limits from a host-owned projection. Billing can update it through an approved publisher seam; host never depends on Billing implementation. |
| `IOrganizationQuota.ReserveAsync(resource, units, operationId, ct)` / `CommitAsync(reservationId, ct)` / `ReleaseAsync(reservationId, ct)` | Atomic capacity reservation with idempotent reconciliation. Persist business/billing usage in PostgreSQL; cache counters are not the financial record. |
| `IModuleAttachmentService.AttachAsync(command, ct)` / `OpenReadAsync(reference, ct)` / `DetachAsync(command, ct)` | Documents-backed opaque attachment references. A registered owner policy reauthorizes the owning resource and organization. Other modules cannot query Documents entities directly. |
| `IModuleEmailQueue.EnqueueAsync(message, ct)` | Named approved templates, organization/recipient intent, registered disclosure policy/source reference, correlation and dedupe identity. Revalidate recipient/source authorization at every attempt; no secrets in logs or unrestricted caller-supplied SMTP destinations. |

The execution request, consumer context, message DTOs, quota reservation and attachment command records must be defined with their owning interface before their first caller is merged. Consumer signatures are host contracts, not runtime service discovery.

### New data boundaries

| Data | Ownership and access |
| --- | --- |
| Work queue, recurring schedules, fan-out delivery state | Host infrastructure. Add explicit coordinator grants/role; do not broaden the existing outbox role to organization business CRUD. |
| Consumer inbox, request-idempotency records and entitlement projections | Host-owned organization data in the same PostgreSQL transaction boundary as affected organization writes; forced RLS, with explicitly reviewed grants. |
| Notification inbox, subscriptions/invoices, products, tickets, chat content | Organization-owned module data; recipient/channel/resource authorization remains additional to organization RLS. |
| Editable billing plans | Platform-owned data and platform-only writes. Expose a reviewed read projection to organization requests. Current `global-read-only` does not supply a writable platform plan catalog. |
| Sessions and password-policy state | Identity-owned tables. Organization role groups are organization-owned; never substitute global Identity roles for workspace grants. |
| Support delegation | Platform-managed grant with an explicitly bounded organization execution policy. Approval does not create membership or a universal RLS bypass. |
| Organization brand | Organization-owned validated settings; platform policy can constrain assets and availability. Product-wide branding remains the fallback. |

### Execution and delivery semantics

- User-initiated jobs revalidate active organization, current membership, required permissions and any resource-specific conditions at execution. Revoked access results in a terminal authorization outcome, not retry forever.
- Consumers of an already committed domain event execute under a registered event-processing grant, not the origin actor's later permission state. Keep that actor as audit provenance. A later membership revocation must not erase a committed invoice or suppress its required bookkeeping; current recipient/channel/subscription visibility is still checked before external disclosure. The registry explicitly classifies each consumer as event-effect, user-command or scheduled-system work.
- Autonomous jobs use an explicit host-controlled system grant with registered module/action/resource limits. Do not fabricate an Owner membership or reuse a platform actor as a tenant superuser.
- Event fan-out creates durable per-consumer delivery records before acknowledging publication. Each consumer atomically commits `(OrganizationId, MessageId, ConsumerKey)` plus its database effect and any next outbox messages.
- If one consumer succeeds and another fails, only the failed delivery retries. Restart after an uncertain commit must not duplicate a committed database effect.
- Disabled-module delivery is paused and visible, retaining payload/history. Re-enable can resume within the configured retention policy; new consumers do not receive historical events automatically.
- Existing event names and payloads retain compatibility mappings. Do not reset processed historical logging-only messages to replay newly introduced side effects.
- SMTP and webhooks are at-least-once. Stable delivery IDs, receiver dedupe guidance and delivery history expose the retry boundary.
- Realtime is a latency optimization. Durable inbox/message history and cursor-based reconciliation recover missed pushes. Authorization is current on connect, subscribe and event delivery; organization switching invalidates prior subscriptions. Bytes already in flight cannot be recalled.

## 5. Module requirements

| Subsystem | Required behavior | Additional policy boundaries |
| --- | --- | --- |
| Documents | Reusable owner-aware attachments, validation, scan status/quarantine, orphan reconciliation, explicit retention execution, archive/restore and safe references. | API-mediated bytes remain the default. Quarantined or unscanned files cannot become available when scanning is required. |
| Notifications | Per-recipient inbox, unread count, mark-one/all-read, developer-registered event consumers, realtime invalidation and a reusable bell/inbox surface. | Current user scope, no arbitrary recipient lookup; optional email uses approved templates. Recipient-configurable delivery preferences/subscriptions are outside this initial parity scope. |
| Webhooks | Per-organization subscriptions, exported event catalog, HMAC signatures, encrypted secrets, delivery attempts, test delivery and controlled redelivery. | Only explicitly exported event DTOs; SSRF/redirect/DNS controls apply to every attempt, including tests and redelivery. |
| Billing | Editable plans, term subscriptions, Draft/Issued/Paid invoice workflow, immutable issued amounts, usage snapshots/overages and scheduled invoicing. | Billing anchor/proration/currency rounding/tax and expiry grace are decisions, not inferred defaults. No gateway or automatic charge is included. |
| Catalog | Brands, hierarchical categories, products, SKU, exact money, stock adjustments, ordered images, search, pagination and recoverable lifecycle. | Prevent category cycles, duplicate organization SKUs, cross-organization attachments and conflicting stock changes. |
| Tickets | Organization-local sequential display numbers, priority, reporter/assignee, comments, legal transitions, reopen and archive/restore. | Closed records reject edits/comments until reopen; assignment and comments require current authority. No SLA/escalation system. |
| Chat | Channels, 1:1/group messages, membership, replies, attachments, mentions, reactions, pins, archive, full-text search and ephemeral typing. | Channel membership and message visibility are additional controls beyond organization RLS. Search follows exactly the read policy. |
| Identity | Durable session registry and revocation UI, organization groups with grant ceilings, optional advanced password policy and constrained support delegation. | Preserve recent assurance, last-admin/Owner protections and organization/platform separation. Password expiry is configurable, not imposed as a new default. |
| Organization administration | Validated brand settings and plan lifecycle status/administration integrated through stable contracts. | Provisioning remains shared by default; expiry policy must state exactly which endpoints remain accessible. |
| Auditing/email | Redacted domain diffs, security investigation, bounded optional HTTP metadata capture, explicit retention and durable template email. | Never enable blanket body/credential capture. Retention does not give ordinary runtime roles DELETE over immutable audit data. |

Owner-bound attachments are distinct from standalone Documents. Existing document list/search/metadata/download/archive/restore/delete paths must apply the attachment owner's policy; `documents.read` alone never reveals a private-channel or otherwise restricted attachment. Missing owner policy/module fails closed. Standalone documents retain their existing organization permissions. Archival of an owner makes its attachments unavailable except through that owner's authorized recovery workflow.

### Supported optional combinations

| Installed capability | Required module dependency | Behavior without optional integrations |
| --- | --- | --- |
| Notifications | None | Inbox works without Chat/Billing; only installed publishers contribute events. Email absent means inbox-only delivery. |
| Webhooks | None | Only explicitly exported events from installed modules are selectable; no historical event backfill. |
| Billing | None | Host entitlement projection works without Notifications/SMTP. Invoice email and inbox are unavailable when their adapters/modules are absent. |
| Catalog | Documents | Full catalog includes product images; enabling Catalog requires Documents and an available storage adapter. |
| Tickets | None | Ticket workflows work without Notifications; no notification inbox is promised in that combination. Attachments are outside the initial Tickets scope. |
| Chat | Documents | Core messaging and attachments require storage. Notifications is an optional mention-inbox integration; full parity profile includes it. |
| Documents | No business module; storage adapter required | Existing standalone behavior remains. Owner-bound attachment reads fail closed when owner code is disabled. |
| Quotas/entitlements | No Billing dependency | Host configuration is the default provider. Quotas default to Disabled; Enforced resources require an explicit finite limit or explicit unlimited setting. Installing Billing adds a projection publisher, not a kernel dependency. |

All six new modules plus Documents/storage constitute the full application parity profile. Required dependency enablement is explicit and atomic; missing prerequisites reject composition before launch. Optional integrations may disappear without making the underlying module unusable.

## 6. Permissions and defaults

Module permission prefixes are `notifications`, `webhooks`, `billing`, `catalog`, `tickets`, `chat` and existing `documents`. Add operation permissions only where a distinct grant changes authority; scope them to current organization and resource. Concrete IDs and actors are frozen in each phase plan.

New grants default to deny except self-service notification/session operations and explicitly documented safe module defaults. At organization creation the existing setup seeds Owner grants from the then-current catalog; current runtime resolution reads those persisted grants rather than dynamically expanding Owner access. New module installation therefore requires the explicit audited, idempotent Owner/catalog reconciliation in task S6 before claiming existing-organization support. Preserve configured non-Owner grants and test that no non-Owner can delegate authority above their own grant ceiling. Platform permissions use the separate published `platform.resource.action` catalog.

An exported webhook event is not an exported assistant tool. New module operations remain absent from assistant execution unless a separate scoped task adds reviewed read-only descriptors/adapters. No new write executor is included.

## 7. Decisions that gate only affected work

| ID | Decision | Proposed engineering position | Blocks |
| --- | --- | --- | --- |
| D1 | Autonomous execution/delegation authority | Host-registered operation grants, audited system principal, current resource validation and no global bypass. | Async business execution and support delegation implementation. |
| D2 | Billing policy | Require explicit plan currency, anchor/timezone rule, rounding, proration choice, tax responsibility and grace/access policy. Never infer commercial defaults. | Issued invoices, automatic expiry enforcement and commercial release; domain/storage work can proceed against explicit fixtures. |
| D3 | Retention/scanning/content policy | No automatic permanent purge until durations, legal hold requirements, scanner failure behavior and operator authority are configured. | Destructive retention and a claim that scanned content is safe. |
| D4 | Support impersonation/delegation UX | Explicit target organization, permitted actions, reason, short bounded expiry, recent assurance, visible banner, full initiator/effective-actor audit and revocation. | Support-delegation workflow only. |
| D5 | Optional deployment services | PostgreSQL durable scheduler first; SignalR with an optional shared backplane and HybridCache-compatible distributed adapter. Verify licenses and maintained versions before introducing packages. | Adapter installation/production configuration; not domain specifications. |
| D6 | Dedicated PostgreSQL placement | Separate workstream using existing placement seam; implement provisioning, routing, worker and operational qualification together. | Dedicated-placement parity only. |
| D7 | Presigned/public object access | Separate ADR; default remains opaque references and API-mediated bytes. | Direct-to-storage/public URL mechanism parity only. |
| D8 | Cloud IaC target | Select deployment provider, region/stamp model, secrets and recovery requirements before writing provider-specific IaC. | Cloud-provider deployment parity only. |

These are explicit preconditions for dependent implementation tasks. They do not block drafting this plan or independent module work. Resolve a decision with the human when it materially changes business behavior; ordinary file layout and local fixture choices belong to the implementation lead.

## 8. Acceptance and evidence

Every phase must produce a usable vertical slice, not just infrastructure declarations or mock screens. Acceptance includes:

- Given organizations A and B, an A user cannot read, mutate, attach, search, subscribe to or receive B data through HTTP, raw SQL, background jobs or realtime.
- Given access revoked after enqueue/connect, future execution/subscription/delivery uses current authority and fails safely; it never trusts saved permission snapshots.
- Given concurrent retries and a crash between effect/acknowledgement, database effects remain deduplicated and external delivery exposes its at-least-once semantics.
- Given a module disabled with retained rows and pending work, its API/Web/work handlers become inactive, no data is dropped, and re-enable behavior is explicit.
- Given a failed write, no successful mutation response, quota charge, orphaned public attachment or next event is reported as committed.
- Given a renamed generated application or backend-only output, contracts, module composition, migrations and selected checks still work without original namespace assumptions.

Use module domain/application tests for rules, real PostgreSQL and production HTTP fixtures for isolation/atomicity, component/Storybook tests for UI states, and browser tests for complete flows. Mocks cannot establish database or backend authorization guarantees.

**Current verification record:** repository inspection and external documentation comparison completed; no product changes, build, runtime feature qualification or performance claim has been made. Implementation checkboxes remain unchecked.

## 9. Comparison sources

FullStackHero documentation consulted on 2026-10-23:

- [Module inventory](https://fullstackhero.net/docs/modules/), [architecture and stack](https://fullstackhero.net/docs/getting-started/introduction/).
- [Files](https://fullstackhero.net/docs/modules/files/), [Notifications](https://fullstackhero.net/docs/modules/notifications/), [Webhooks](https://fullstackhero.net/docs/modules/webhooks/).
- [Billing](https://fullstackhero.net/docs/modules/billing/), [Catalog](https://fullstackhero.net/docs/modules/catalog/), [Tickets](https://fullstackhero.net/docs/modules/tickets/), [Chat](https://fullstackhero.net/docs/modules/chat/).
- [Identity](https://fullstackhero.net/docs/modules/identity/), [Auditing](https://fullstackhero.net/docs/modules/auditing/), [tenancy](https://fullstackhero.net/docs/modules/multitenancy/).
- [Jobs](https://fullstackhero.net/docs/cross-cutting-concerns/background-jobs/), [caching](https://fullstackhero.net/docs/cross-cutting-concerns/caching/), [idempotency](https://fullstackhero.net/docs/cross-cutting-concerns/idempotency/), [flags](https://fullstackhero.net/docs/cross-cutting-concerns/feature-flags/), [quotas](https://fullstackhero.net/docs/building-blocks/quota/).

Recheck these sources before claiming later full parity; the comparison target can evolve.
