# TryKatch S2 Authorized Realtime Implementation Plan

> Execute the approved S2 scope task by task using the existing Sol/Luna workflow, targeted RED/GREEN validation and one final independent integrated review. This is an implementation refinement of the approved platform-services plan, not a new product scope.

**Outcome:** Installed modules can persist small authorized invalidations through committed event consumers. Cookie-authenticated browsers receive them through SignalR and recover missed changes through authoritative HTTP history.

**Authority:** [Parent S2 plan](2026-10-23-parity-02-platform-services.md), [design](../specs/2026-10-23-feature-parity-design.md), [frozen contracts](../../../templates/trykatch/docs/specs/feature-parity-contracts.md#7-cache-and-realtime--s1s2) and [ADR 0018](../../../templates/trykatch/docs/adr/0018-scheduler-cache-and-realtime-adapters.md). Paths below are relative to `templates/trykatch/` unless stated otherwise.

## Execution boundaries

- Start from latest `origin/develop` in the clean isolated `feat/authorized-realtime` worktree. S1 PR #134 must merge and this branch must be cleanly rebased before product implementation. Preserve the primary checkout.
- Sol owns authorization, migrations, role grants, integration and release decisions. Scoped Luna workers may own contracts/pure validation or browser reconciliation after interfaces are fixed. Workers do not spawn agents. One local .NET writer at a time.
- Module contracts contain no SignalR, Redis, EF or Identity types. PostgreSQL owns history and scope. Redis and browser state never supply permission or session authority.
- Source publication persists intent inside an E3 consumer transaction; network delivery follows commit. Preserve the existing Projects typed and legacy event publications.
- Reuse Identity for the session foundation needed by S2. A1 later adds session administration and password policy; those surfaces are not implemented here.
- Installed modules remain an explicit immutable registry per running host. Module removal requires draining/restarting the old deployment. Verify that real boundary; do not invent hot module loading or prove it with a mutable mock.
- Existing RustFS storage remains unchanged. The user explicitly retained the already merged PR #131/ADR 0019 solution after answering the older MinIO prompt.

## Lead design decisions

**History and delivery.** An E3 consumer calls `IModuleRealtimePublisher` to append organization-owned history in the same transaction as its inbox marker and effect. A per-organization counter row serializes cursor allocation and append; a database sequence/timestamp cannot safely establish commit order. Identical repeated event identity is a no-op; changed content under that identity rejects the transaction.

Each API maintains a bounded registry of its own live connections and polls committed history. Every individual send uses fresh session, membership, permission, installed-catalog and resource-policy checks. It never broadcasts sensitive payloads to generic groups, retains scoped EF services or queues preauthorized values for later delivery. The check-to-send/in-flight boundary in the approved design still applies; strict serialization of revocation commits with network receipt is not claimed.

The approved optional Microsoft Redis backplane remains available in Distributed mode and receives separate real cross-node qualification. Local-owner sends bypass Redis in the pinned implementation, so this adapter is redundant for the reference relay. This explicit compatibility cost is smaller than adding a durable cross-node connection registry merely to make Redis necessary. HTTP history remains authoritative through outages.

**Session and workspace.** Identity owns a persistent session row bound to the issued session ID, actor, security-stamp epoch, expiry, revocation and current workspace generation. Issue/register centrally during authenticated cookie sign-in; validate current state on HTTP and each realtime attempt; revoke centrally on sign-out. Preserve renewal IDs. Missing, old or revoked rows require reauthentication and are never recreated by delivery. Workspace select/clear and invitation activation/accept rotate the durable generation, including A→B→A. Platform suspension remains separate from organization membership.

**Payloads and cursors.** The first version carries only invalidation references: module/event/schema/event ID and a registered organization/user/resource audience. No arbitrary JSON, message body, entity or caller-supplied organization/group/cursor is accepted. HTTP history scans at most 100 rows and advances after the last scanned row even when every row is filtered. Protected continuations bind organization, actor, session and workspace generation. A null history cursor starts at zero; initial state separately provides a head cursor paired with full domain-query refetch. Invalidated cursors require an explicit reset/refetch outcome. No history deletion or retention window is invented.

**Bounds and transport.** Start with 256 connections per replica, 16 subscriptions per connection, 16 concurrent senders, a one-second polling target, 100-row scans, two-second sends, five-second cooperative authority deadlines, 2 KiB invalidations and 4 KiB incoming hub messages. Coalesce pending work and retain only one sender per connection. Cancel and abort slow transports; never abandon scoped resource-policy work. Cookie-only WebSocket connections require exact same-origin validation; applicable negotiation/transport HTTP writes require explicit antiforgery validation. Do not hold the organization transaction around an upgraded connection's lifetime.

## Task 0: Qualify and pin the patched runtime and adapters

**Owner:** Sol; bounded isolated dependency work may be delegated.

- [x] Verify official SDK 10.0.402 checksum and actual .NET/ASP.NET Core 10.0.13 runtimes. The former local wrapper's 10.0.9 runtime cannot qualify the new SignalR feature.
- [x] Isolated combined graph: SignalR Redis/client 10.0.13, HybridCache 10.11.0, Redis cache 10.0.13 and MessagePack 2.5.303; normal/locked restore, build with zero warnings/errors, 11 permitted MIT package licenses, transitive audit without findings.
- [x] Live isolated pinned authenticated Valkey: actual nonlocal delivery, application-prefix isolation, failed outage send/no receipt, unchanged endpoint restart, existing and new client recovery. Earlier random-port restart failures are fixture diagnostics, not passing evidence or established adapter defects.
- [x] Update root/template `global.json` to 10.0.402, all applicable root/template CI/release selectors to 10.0.4xx, API/Migrator SDK/runtime image tags and verified digests, and current prerequisite/reproducibility docs. Preserve historical evidence versions.
- [x] Add the server backplane dependency only to the API host, client dependency only to affected test/support projects, and `@microsoft/signalr` 10.0.11 only to the web host. Restore the actual graph normally and locked; audit its resolved licenses/advisories. No vendor dependency enters module abstractions or the web SDK.

**Expected:** Real builds/tests execute on the fixed runtime; no graph conflict, unreviewed dependency or generation-time Redis connection. Isolated vendor checks do not qualify application authority, OS replicas or browsers.

## Task 1: Freeze module-neutral contracts and finite registration

**Owner:** A bounded Luna may own `Modules.Abstractions/Realtime/`, its pure tests and the explicitly assigned descriptor properties. Sol integrates catalog/runtime binding.

**Proposed files:** `Realtime/ModuleRealtimeContracts.cs`, `ModuleRealtimeRegistrationRules.cs`; descriptor additions in `TrykatchModule.cs` and `Execution/ModuleExecutionContracts.cs`; `tests/Trykatch.UnitTests/RealtimeAudienceTests.cs`.

Freeze these fields before adapters consume them:

| Type | Fields / signature |
| --- | --- |
| `ModuleRealtimePolicyRef` | OwnerModuleId, PolicyKey, positive PolicyVersion |
| `ModuleRealtimeResourceRef` | Registered ResourceKey, nonempty ResourceId |
| `ModuleRealtimeAudience` | Policy, kind Organization/User/Resource, optional UserId and Resource |
| `ModuleRealtimeNotification` | ModuleId, EventKey, positive SchemaVersion, nonempty EventId, Audience |
| `IModuleRealtimePublisher` | `Task PublishAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default)` |
| `ModuleRealtimeProducerRef` | ModuleId, OperationKey, positive OperationVersion |
| `ModuleRealtimeAudiencePolicyDescriptor` | Owner/key/version; finite allowed audience kinds, owner resource keys, required recipient permissions, event/version pairs and exact system producer refs |
| `ModuleRealtimeAudienceContext` | Host-injected OrganizationId and ActorId |
| `IModuleRealtimeAudiencePolicy` | `Task<bool> AuthorizeAsync(ModuleRealtimeAudienceContext context, ModuleRealtimeAudience audience, CancellationToken cancellationToken)` |
| `ModuleRealtimeAudiencePolicyBinding` | Exact policy ref and explicitly registered scoped adapter |

- [x] RED: malformed/unknown/wildcard/duplicate policy, event, user and resource selectors; wrong ownership/version; absent module/permission/resource; excessive arrays; missing/duplicate bindings; producer/grant mismatch. Descriptions must not leak user/resource IDs.
- [x] GREEN: validate bounded stable keys and exact installed declarations; add `RealtimeAudiencePolicies` and reciprocal `AllowedRealtimePolicies` grant metadata. Committed-event producers require explicit owner consent and a finite installed grant. Empty recipient permissions are permitted only for explicit self-service user/resource policies with a current adapter, never an unrestricted organization broadcast.
- [x] Verify focused unit tests and directly affected catalog/operation registration cases. Expected: every negative case fails closed; no runtime assembly scanning or arbitrary group names.

## Task 2: Add authoritative history, publication and opaque cursors

**Owner:** Sol for schema, RLS, transaction/provenance and migration decisions.

**Proposed files:** Infrastructure `Realtime/ModuleRealtimePublisher.cs`, history store/mapping; Application history binding/port; Infrastructure cursor protector consumed by the API; forward application migration; reserved-resource/profile/policy definitions.

- [ ] RED: cross-organization/raw-role access; missing actor/shared transaction; forged producer; identical duplicate/conflict; consumer rollback; concurrent first cursor; delayed earlier commit; read-only publication; filtered-page progress; tampered/foreign/future cursor.
- [x] GREEN: host-reserved `app.realtime_streams` counter and append-only `app.realtime_history`, organization indexes and exact grants. Force RLS; history runtime access is SELECT/INSERT, not DELETE/UPDATE. Reuse the actual shared organization transaction and authoritative `ModuleWorkExecutionContext`; persist no network side effect.
- [x] Allocate the counter with a transactionally serialized row upsert and append immutable event identity. Validate actual current database scope and reciprocal installed grant/policy. Let rollback discard both history and counter changes.
- [ ] Read bounded ordered pages through current recipient policy, with continuation after the last scanned position. Protect cursor scope and bound input before decoding. Retain history; explicitly handle reset without treating missing history as successful delivery.
- [ ] Verify real PostgreSQL migrations/role inspection, concurrency/rollback and focused publication/history tests. Update every directly affected hand-built schema fixture. Expected: no uncommitted event, skipped late commit, private audience disclosure or inspector exception.

## Task 3: Add the session foundation and workspace revocation boundary

**Owner:** Sol security integration; isolated registry work may be delegated after its exact brief is fixed.

**Proposed files:** Identity `IdentitySessionRegistry.cs`, `IdentitySessionRecord.cs`; Application `Identity/AccountSessions.cs`; Identity forward migration; cookie events and workspace/invitation controllers.

- [x] RED: independent devices, renewal, missing/legacy/tampered rows, logout/revocation, expiry, stamp/MFA changes, workspace A→B→A, same-request invitation sign-in and restart continuity.
- [x] GREEN: hashed issued session identity, actor/stamp/created/last-seen/expiry/revocation/workspace generation; fresh validity checks with throttled last-seen writes. Narrow parameterized registry operations must not flush unrelated tracked Identity state.
- [x] Wrap existing cookie/security-stamp events. Capture trustworthy issued/current session in an Identity request feature for centralized sign-out and invitation workspace binding. Rotate workspace generation before writing the corresponding cookie; fail closed rather than resurrect missing sessions.
- [x] Verify focused session tests plus directly affected `AuthenticationSecurityTests`, `AccountSecurityStepUpTests`, invitation activation and `ProductionIdentityContinuityTests`. Expected: revoked sessions cannot supply HTTP or realtime authority; MFA, last-admin and organization/platform separation remain intact.

## Task 4: Secure the hub, owner relay and HTTP reconciliation

**Owner:** Sol host integration. No public module API receives `IHubContext` or connection-group management.

**Proposed files:** API `Realtime/ModuleRealtimeHub.cs`, audience authorizer, connection registry/relay, state/history endpoints and configuration; optional Redis backplane composition, sanitized health/metrics and reverse-proxy settings.

- [ ] RED: unauthenticated/cross-origin/missing-antiforgery negotiation, foreign user/resource, arbitrary group/unknown method, invalid selectors, session/stamp/membership/permission changes after connect, workspace ABA and real module-absent restart.
- [ ] GREEN: cookie-only WebSocket hub; explicit atomic subscription replacement; host-injected organization/user scope; registered current resource adapter; fresh authority before each individual bounded send. No scoped service or permission snapshot lives with a connection.
- [ ] Add stable `Realtime_GetState` and `Realtime_GetHistory` operations. Disabled/default mode performs no adapter network work; durable append/history remain supported. Distributed mode uses explicit authenticated finite configuration and isolated channel prefix. Generation cannot require a live backend.
- [ ] RED/GREEN: commit versus rollback, reconnect gaps, duplicate/out-of-order wakeups, filtered history, slow-client backpressure, cancellation/scope disposal, actual two OS replicas, live backplane outage/recovery and separate nonlocal routing. A backplane failure cannot erase history or fabricate current access.
- [ ] Verify actual Vite/deployment proxy WebSocket upgrades and exact Origin after trusted forwarding. Export only closed diagnostic dimensions and sanitized health. Expected: no unbounded pending queue, leaked scoped work, credential disclosure or stronger-than-tested availability claim.

## Task 5: Connect the Projects reference and browser lifecycle

**Owner:** Sol E3/source integration; a bounded Luna may own the SDK/host browser lifecycle and explicitly assigned stories/tests after backend DTOs are fixed.

- [ ] RED: existing `projects.created` commits one reference invalidation through a registered E3 consumer; rollback/retry/uncertain acknowledgment preserves inbox/history atomicity. No change to existing legacy/typed event publication shapes or count.
- [ ] GREEN: Projects read policy, committed-event operation/consumer and finite producer grant. Send only the project reference. Use an additive module minor version consistently in its web descriptor/catalog lock; preserve event v1 and current user permissions.
- [ ] Include affected existing event fanout/worker/Phase 1 exit tests and default/backend coordinator startup. Do not install a consumer whose required coordinator is silently absent.
- [ ] RED/GREEN SDK tests: serialized HTTP page processing, bounded deduplication, unordered/duplicate push coalescing, reconnect/periodic repair, failed HTTP without cursor advancement, and late callbacks/responses after session/workspace generation changes.
- [ ] Add module-owned query invalidation contributions using actual generated query keys and any still-consumed local keys. The host never imports a module's private UI. Pushes wake HTTP reconciliation rather than advancing cursors directly.
- [ ] Connect the real cookie-authenticated vendor client in AppShell; stop/cancel old work and clear relevant organization query state on logout/workspace/authority loss. Backend-only products omit React and the vendor browser client.
- [ ] Consult real shell/Projects stories before edits; maintain loading, permission and failure interactions without adding unrelated controls. Verify focused SDK/component/Storybook interaction/accessibility checks and actual `e2e/realtime.spec.ts` with two clients, revocation, workspace switching and missed-push recovery. Regenerate OpenAPI/client/assistant metadata with actual tools and inspect drift.

## Task 6: Qualify, document, review and merge

- [ ] Update ADR 0018 implementation status, canonical contracts, roadmap/acceptance matrix and `docs/specs/evidence/parity-platform-services.md`. Record the session foundation as partial A1 work, not completed administration.
- [ ] Add English/French operations guidance: required forward migrations and legacy reauthentication, default/backend behavior, coordinator/backplane configuration, bounds, restart/module-removal procedure, proxy/Origin/antiforgery, cursor recovery and the precise revocation/in-flight boundary. Do not invent retention or production HA guarantees.
- [ ] Fresh actual packaged CLI renamed React and backend-only outputs: normal/locked restore, affected runtime/build/doctor/contracts and selected authority/transport/browser checks. Run targeted checks by default; established CI supplies its broader gates. Explain any additional broad run before executing it.
- [ ] Sol inspects actual diff and evidence, then one fresh independent integrated review uses the existing reviewer workflow plus the supplemental ECC checklist. Fix substantive findings with focused regressions in one bounded pass; no duplicate review workflow.
- [ ] Commit/push a neutral branch, open/attach the PR, wait for successful exact-head required checks and merge under standing authorization. Save verified local project memory, then begin S3 in a new clean worktree from latest `origin/develop`.

## Review focus

1. A copied cookie, sliding renewal or A→B→A transition must not resurrect revoked live access.
2. A delayed commit or entirely filtered history page must not create an unrecoverable cursor gap.
3. Current recipient authority must precede each send, including retained history, private resources and backend failures.
4. Slow transport/module callbacks must not leak or abandon scoped EF work or accumulate unchecked payloads.
5. Vendor, in-process, OS-replica, generated-product and real-browser evidence must remain distinct; a successful Redis publish is not receipt or authorization.

## Execution evidence

S1 merged as `6140050` after exact-head `e7703d6` CI [40069411903](https://github.com/TryKatch/TryKatch/actions/runs/40069411903) passed all selected gates. This branch was cleanly rebased before S2 product edits.

The actual SDK is 10.0.402 with both shared runtimes at 10.0.13. Root/template pins, image digests and selectors are updated; the four existing reproducibility assertions pass. Actual application NuGet normal/locked restores and the 36-project transitive audit pass; all 14 newly resolved package/version pairs declare MIT. Web normal/locked restores use pnpm 10.17.1; all 18 new package/version pairs declare permitted MIT, BSD or Unlicense licenses. The existing fast-uri 3.1.8 override triggered four advisories; updating it to 3.1.9 yields zero audit findings. These are dependency checks, not application authority, transport or browser acceptance.

The contract stub run reproduced 36 failures out of 38 cases, with zero skips. The first implementation run exposed one malformed-null exception mismatch; it also reproduced four expected history-schema failures for missing model/grants. The final focused group passes 85 cases with zero skips, including contract registration, host schema, existing catalog, execution, email and model composition checks. Primary keys beginning with OrganizationId now satisfy the existing indexed-organization invariant without requiring a redundant secondary index. Real PostgreSQL migration and publisher/history behavior remain pending.

The forward `20261024064323_AddOrganizationRealtimeHistory` migration was scaffolded by the actual EF tool. A real migrator case first failed on pending model changes; after applying the forward migration and exact policies, all 35 selected PostgreSQL cases pass with zero skips (two new migration/RLS cases and all 33 directly affected inspection cases). The hand-built inspection fixture now applies this migration and exact grants. Existing OpenAPI/client/assistant generation succeeds with no generated drift after the fast-uri patch. This qualifies the schema, not publication, session authority or live delivery.

The two new migration cases pass again with zero skips after adding direct history-only foreign/missing-actor attempts. Module doctor is healthy and all 14 directly affected CLI development cases pass.


Publication qualified against real PostgreSQL: all eight initial cases first failed on the missing implementation and then passed with zero skips. They cover current registered committed-event provenance, actual shared transaction/GUC checks, precommit invisibility, business/history rollback, identical duplicate no-op, changed-identity conflict, organization partitioning, competing first commits and bounded pages. The scope-local execution provenance is a synthetic host seam here; the full E3 consumer path is qualified in Task 5. A per-organization transaction advisory lock protects the duplicate check before the first counter row, followed by the atomic counter upsert; both locks remain until business commit.

All 21 affected cache/queue/executor composition cases pass with zero skips. Two additional focused history checks pass after adding writable/missing-shared/changed-actor read rejection, negative/future positions and terminal empty-page progress. The cursor codec first failed all nine initial tests; its final 16-case selection passes with zero skips, including scope/actor/session/generation isolation, malformed/tampered input, purpose isolation, incompatible protected formats and another codec using the same protection provider. The bounded protected binary format uses the existing host Data Protection provider; it never grants session or recipient authority. API registration and current recipient-filtered history acceptance follow Tasks 3/4. No new DI/transport dependency enters module contracts.


Task 3 adds the actual EF-scaffolded Identity forward migration `20261024071616_AddDurableAccountSessions`, an Identity-only SELECT/INSERT/UPDATE registry and neutral Application proof/state ports. Cookie validation wraps the existing stamp callback; normal framework renewal conditionally extends an existing current row. Both captured ticket and durable expiry must independently remain current. Missing/revoked rows never become inserts. Fresh sign-in replaces only this browser's previous row in the issuance transaction. Narrow SQL reads the current user stamp independently of tracked Identity entities, and throttles last-seen updates during validation and renewal.

The first schema test failed on the absent relation. The initial registry/lifecycle group reproduced 18 failures out of 19 cases; 17 passed after implementation, and the two remaining timestamp-reader test errors were corrected and passed separately. Additional focused cases qualify legacy/unregistered identities and actual PostgreSQL lock waiting during concurrent revocation. Workspace tests reproduced four missing bindings; all four flows now pass across the focused runs: invitation acceptance, A→B→A/reselection/clear, failed-login clearing, ordinary membership removal, and same-request activation while another account was signed in. Existing archive-before-delete and protected administrator rules are preserved. MFA grants and pending enrollment now use the durable hash rather than the raw cookie session ID; the hash and renewal last-seen cases reproduced failures before their fixes and both pass.

After controller integration, all 38 directly affected HTTP cases pass with zero skips, including authentication/OpenID Connect, MFA, invitation activation, organization middleware, bearer/antiforgery separation, and actual production key-ring maintenance/restart continuity. The earlier 65-case affected run exposed one missing custom fixture grant and the old MFA expectation of 403 for a stale cookie. The exact custom grants were repaired; the stale cookie now correctly receives 401 before step-up. All 33 affected role-inspection cases pass across the original run and its focused correction. Twelve architecture cases pass; module doctor remains healthy. These are local session-foundation checks; hub/relay, recipient-filtered history, browser behavior, packaged outputs and final integrated review/PR/CI remain pending.

The final renewal/migrator selection passes all eight cases with zero skips after the last-seen change: captured-ticket expiry, monotonic renewal, all invalid row states, both real lock-waiting revocation races, normal/stamp-driven cookie renewal and the actual migrator applied twice with exact role inspection. Log: `session-final-renewal-and-migrator.log`.


The current-recipient history facade now has provider-neutral Application ports and explicitly registered scoped Infrastructure adapters. Eight focused recipient-policy cases failed on the missing implementation and then pass with zero skips. They cover current human scope, permissions, exact owner/event/schema/policy, user self-targeting, resource hints, changed resource visibility, absent/changed policies, and propagated callback failure/cancellation. These are unit policy checks, not current database membership or session evidence.

Two real PostgreSQL facade cases also failed before implementation and now pass with zero skips. A fully filtered first page advances to scanned position 100 with HasMore; the final authorized resource is disclosed at position 101, then suppressed on a later read after its backing project is removed. The callback queries the actual scoped connection after the raw reader is disposed. Writable/unshared/changed-actor and negative/future-page guards remain enforced. Membership/execution initialization in this fixture is synthetic host setup, so these cases do not establish HTTP or live recipient admission. All 21 directly affected cache/queue/executor composition cases pass. Logs: `recipient-authorization-red.log`, `recipient-authorization-green.log`, `recipient-history-red.log`, `recipient-history-green.log` and `recipient-composition-regressions.log`. HTTP scope-bound cursor handling and hub/relay remain pending.


Task 4 HTTP checkpoint: Realtime_GetState/GetHistory use the existing cookie-only organization read-only pipeline and fresh durable session binding. Cursors bind actor, organization, device hash and workspace generation; malformed, oversized, future and cross-scope continuations produce explicit realtime_reset_required without items. State returns the current head independently of null-cursor history. A final durable binding check rejects a workspace change during policy work. Default Disabled mode still exposes history without live adapter setup. Five options tests first failed on the missing validator; six HTTP cases first failed on absent routes. All 11 then pass with zero skips (realtime-http-options-green.log). Actual OpenAPI/client/assistant generation succeeds (realtime-http-generation.log); hub, relay, proxy and browser qualification remain pending.


Task 4 admission checkpoint: cookie-only WebSocket routing has no organization transaction around the upgraded lifetime. Exact single Origin is checked against Application:PublicUrl, or the trusted request scheme/host; negotiation writes explicitly validate antiforgery and reject query access tokens. Three HTTP cases first fail on absent routes and then pass, covering anonymous/cross-origin/missing-CSRF rejection, WebSocket-only negotiation and configured external origin. The bounded local registry keeps detached binding/subscription data and only a weak reference to the framework lifetime feature. Eight pure tests first fail on the absent registry then pass (in the combined authority baseline), including remove-before-abort, copied immutable subscriptions, one sender and weak request-graph retention. The initial fake lifetime omitted the framework interface setter; that compile defect was corrected before the meaningful RED run.

Fresh RealtimeAuthority attempts reuse BeginReadOnlyAsync and current session, membership, permissions and Shared placement. Actual PostgreSQL tests inspect both enlisted contexts, READ COMMITTED/read-only, exact runtime role and GUCs; subsequent attempts reject stale rows, captured-ticket expiry, stamp/membership/org changes and workspace ABA. The monitor bounds concurrent work and awaits callbacks fully. Seven actual application WebSocket cases use TestServer and real PostgreSQL; they qualify live abort on revocation/stamp/expiry/membership/org/ABA and reject unknown methods/policies. All 18 authority/socket cases fail before implementation and pass afterward with zero skips (realtime-authority-socket-red.log / realtime-authority-socket-green.log). This checkpoint only monitors authority; committed live delivery, resource subscriptions, Redis, OS replicas, proxies and browser behavior are not yet qualified.


Task 4 delivery/configuration checkpoint: the local owner scans one materialized page per poll and retains one sender per connection. Every matching invalidation opens a separate fresh recipient scope, rechecks the current subscription and notification policy, then disposes all database/resource work before the bounded local transport send. Send cancellation quarantines/aborts the connection; failed sends do not advance past the unsent row. Filtered/scanned positions still advance. Policy work that ignores cancellation is awaited before disposing its scope; no deadline abandons EF work.

Seven isolated orchestration tests fail before delivery and pass afterward. They use scoped fakes, not PostgreSQL membership/resource proof. Root corrected the worker fixture's framework setter/accessibility/Options shadowing, replaced unnecessary reflection proxies with explicit small interface fakes, retained fake transports strongly, made parallel history observations thread-safe, and aligned page/scope/cancellation expectations with one-page polling and standard propagated cancellation. Four diagnostics tests fail before implementation and pass afterward; health descriptions omit credentials/endpoints/vendor errors, and telemetry exports only closed mode/outcome dimensions. The optional health probe lazily owns a separate connection from the framework backplane and has fixed vendor timeouts; neither supplies recipient authority.

Five backplane settings/composition cases reproduce four missing implementation failures and one existing successful default/generation path, then all pass. Normal Distributed mode requires a single authenticated endpoint, disallows Sentinel discovery, forces finite timeouts and isolates literal channels by explicit application namespace and hashed environment. Disabled/LocalOnly/generation never parse or register the Redis adapter. Actual live backplane routing remains unqualified here. All 11 delivery/diagnostics tests pass with zero skips; all 21 directly affected host WebSocket/HTTP/configuration cases pass after integration. Logs: realtime-backplane-settings-red/green.log, realtime-delivery-red.log, realtime-diagnostics-red.log, realtime-delivery-diagnostics-green.log and realtime-delivery-host-regressions.log. Projects E3 reference, actual live resource policy, OS replicas/backplane, proxies, browser, packaged outputs and final integrated review/PR/CI are still pending.


Task 4 proxy/configuration checkpoint: Vite forwards API WebSocket upgrades without changing Origin/Host. Deployment nginx forwards Upgrade/Connection and preserves the external Host including its port. AppHost forwards explicit bounded realtime settings, provisions a separate pinned authenticated Valkey only for Distributed mode, and supplies the real Vite HTTP public origin in React run mode unless the operator supplied Application:PublicUrl. The source-pinned Aspire endpoint environment reference adds no WaitFor edge; it does not create a startup wait cycle. Published origins remain explicit. The optional compose.realtime.yml requires an application namespace/password and isolates its bounded nonroot backend; RustFS and the cache adapter remain unchanged. API appsettings makes the existing Disabled default explicit.

Actual AppHost build succeeds with zero warnings/errors (realtime-apphost-build.log). Existing proxy header contract passes and both React/backend Compose overlays validate quietly with disposable placeholder settings. Those checks do not exercise the new WebSocket forwarding; actual Vite/nginx upgrades, forwarded-origin authority and OS replicas remain pending.

Task 4/5 Ruling: implement the real Projects committed-event reference before final live transport/OS/proxy qualification — those acceptance tests should use real source publication and recipient policy, not a synthetic producer or mutable module registry. All remain one unmerged S2 slice; cost if wrong is sequencing/rework, with no deployment or security boundary relaxed.
