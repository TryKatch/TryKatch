# Authorized realtime invalidations

[Français](realtime.fr.md)

Realtime sends small module-owned references. PostgreSQL history and current HTTP reads remain authoritative. A push wakes HTTP reconciliation; it does not carry a business document or advance a browser cursor. The default is `Realtime:Mode=Disabled`. HTTP state/history and committed-event publication still work in that mode, without a Valkey connection.

## Upgrade and default startup

Apply the forward migrator before starting the new API. Application migration `20261024064323_AddOrganizationRealtimeHistory` adds organization-isolated history/counters; Identity migration `20261024071616_AddDurableAccountSessions` adds durable browser-session authority. Runtime roles cannot update/delete history; counter changes use the organization profile, and only the Identity profile can access session rows. Do not deploy cookie hooks against an unmigrated database.

Existing cookies without a durable session row require sign-in again. The registry stores a session hash, security-stamp epoch, expiry, revocation and workspace generation. Normal renewal extends only an existing current row; it cannot recreate a revoked session. An already connected socket retains its captured ticket expiry, even if later HTTP requests renew the browser cookie. These foundations do not add the planned session-administration UI or a retention policy.

Projects installs one `projects.created` consumer and an exact finite system grant. Its history append and inbox receipt commit together through the existing event worker. Consequently an application with this consumer needs an explicit `ConnectionStrings:trykatch-work`, even when general jobs and live push are disabled. Default AppHost and regular/backend Compose already provide the dedicated coordinator role. Missing/blank configuration fails startup; no owner/organization credential fallback is used. OpenAPI generation validates registrations without requiring the coordinator or backplane to connect.

Projects' `app.projects` DDL is owned by its immutable module SQL migration ledger. Its EF mapping remains available for queries but records the relation as excluded from EF DDL generation in the canonical migration snapshot; this does not relax the host's strict live-schema drift checks. Put future Projects schema changes in new forward-only SQL ledger entries. Keep the existing applied baseline unchanged.

## Select a mode

| Mode | Behavior |
| --- | --- |
| `Disabled` | No hub route, relay or Redis adapter. Authorized HTTP history remains available. |
| `LocalOnly` | Cookie-authenticated WebSocket connections and the local owner relay, without Redis. |
| `Distributed` | Local owner relay plus the standard SignalR Redis adapter, authenticated single-endpoint configuration and an explicit application namespace. |

Set `Realtime:Mode` in AppHost configuration and, for Distributed mode, an explicit `Realtime:ApplicationNamespace`. Distributed mode provisions a separate pinned Valkey resource named `realtime`, using the protected Aspire `realtime-password` parameter. It does not reuse the read-model cache or object-storage service. In React run mode, AppHost supplies the actual frontend public origin unless `Application:PublicUrl` is explicitly configured. Set the public URL explicitly for published deployments.

Compose supports both regular and backend-only applications:

```sh
docker compose --env-file .env -f compose.yml -f compose.realtime.yml up -d --build
# Backend-only output:
docker compose --env-file .env -f compose.backend.yml -f compose.realtime.yml up -d --build
```

The overlay requires protected `TRYKATCH_REALTIME_PASSWORD` and nonsecret `TRYKATCH_REALTIME_NAMESPACE` values. Use a bounded lowercase kebab namespace, identical across replicas of one product/environment and distinct across unrelated products. The private pinned Valkey has no published port, no persistent volume, a 64 MiB limit and `noeviction`; it is a delivery optimization, not a history store or production HA service.

For an externally managed backend set `Realtime__Mode=Distributed`, `Realtime__ApplicationNamespace=<product>` and `ConnectionStrings__realtime=<endpoint>,password=<secret>`. Load the actual secret through the deployment secret store. Exactly one authenticated endpoint is accepted; Sentinel discovery is not supported. Use TLS when the deployment requires transport encryption. Channel isolation combines the namespace with the host environment name. Never put customer IDs, user IDs or secrets in that namespace.

Each API polls and delivers only to its own local connections, repeating recipient checks for each notification. Standard SignalR sends to a locally owned connection bypass Redis. Therefore this reference relay can continue local delivery during a backplane outage; separately routed nonlocal SignalR messages can be lost or fail. Neither outcome changes committed history. Do not infer cluster availability or Redis durability from a completed send call.

## Browser and reverse proxy

The hub is `/api/v1/realtime/hub`; it accepts WebSockets only. Negotiation uses the same HttpOnly application/workspace cookies and `X-CSRF-TOKEN` as existing unsafe browser requests. Query access tokens, arbitrary group membership and client-selected organization IDs are not supported. Browser credentials/tokens must not be copied into local storage.

Preserve the external Host **including its port** and the original Origin through every proxy. Forward WebSocket Upgrade/Connection headers over HTTP/1.1. The supplied Vite `/api` proxy and deployment nginx support upgrades. With multiple proxy hops, every hop needs that configuration. Trust only the immediate proxy through the existing explicit known-proxy settings; clients must not supply trusted forwarded protocol/host values. The server requires one exact HTTP(S) Origin matching `Application:PublicUrl`, or its trusted request scheme/host when no public URL is configured. Do not fix a rejected Origin by reflecting it or weakening the allowlist.

Negotiation and upgrade must reach the same API process, including with Redis ([SignalR hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0#sticky-sessions)). Supplied nginx uses `ip_hash`; other load balancers need equivalent affinity. Shared client addresses can concentrate load, and changed peers/addresses can require reconnect and HTTP repair.

Modules contribute finite subscriptions and owned query keys through the browser SDK. The host uses the real vendor client without importing module-private UI. Backend-only products omit React and the browser client. Pushes, reconnect and periodic repair wake a serialized HTTP drain. Startup/reset pairs the state head with a full owned-query refetch; failed page processing never commits its continuation. Workspace/logout/authority changes cancel the old lifecycle and clear organization query state so late callbacks cannot restore it.

## Current authority and recovery

Every admission, subscription replacement, polling attempt and individual send uses fresh durable-session, workspace-generation, membership, permission and resource checks in a scoped read-only organization transaction. Organization/user subscriptions with a resource hint still need current resource visibility. The Projects adapter follows the same visibility as its read endpoint, including readable lifecycle metadata. An unknown policy/resource, foreign user selector or denied replacement supplies no new access; replacement is atomic.

Logout/revocation, security-stamp changes, captured ticket expiry, inactive membership/organization or workspace A→B→A prevent the old connection from becoming current again. The last authority check precedes a bounded awaited send. Bytes already in flight cannot be retracted; database commit and network receipt are not serialized. Cancellation requests do not abandon a module callback or dispose its database scope while that callback is still running.

`GET /api/v1/realtime/state` returns live-enabled status, current workspace generation and an opaque head cursor. `GET /api/v1/realtime/history` starts at zero without a cursor, or resumes with `?cursor=...`. Pages scan at most 100 rows and advance past filtered rows. Cursors bind organization, actor, device-session hash and workspace generation. A malformed, oversized, future, foreign or obsolete continuation returns HTTP 409 with `realtime_reset_required`; discard it, obtain current state and refetch the owned domain queries. Never parse, construct, log or transfer a cursor across sessions/workspaces.

A missing workspace can instead return middleware HTTP 409 `Workspace context required` before the realtime reader runs. The browser treats that precise outcome, or HTTP 401/403, as loss of the old organization context and resets mounted organization results while preserving the account session. Unrelated conflicts do not revoke the context. Failed owned-query refetches propagate to reconciliation, so neither bootstrap nor a history page adopts its cursor until those refetches succeed.

## Bounds and diagnostics

| Setting | Default | Accepted range |
| --- | --- | --- |
| `MaximumConnections` | 256 | 1–256 per API |
| `MaximumSubscriptions` | 16 | 1–16 per connection |
| `MaximumConcurrentSends` | 16 | 1–16 per API |
| `PollInterval` | 1 second | 100 ms–30 seconds |
| `SendTimeout` | 2 seconds | 100 ms–10 seconds |
| `AuthorityTimeout` | 5 seconds | 100 ms–30 seconds |

Settings belong under `Realtime`. Incoming hub messages are capped at 4 KiB and invalidations at 2 KiB. One sender owns a connection at a time; there is no queue of preauthorized payloads. A failed/timed-out send quarantines the connection without advancing past the unsent row. Clients recover through HTTP. A finite per-API attempt bound also limits fresh authority work.

Readiness includes the sanitized `realtime` check. Disabled/LocalOnly do not probe a backend. Distributed mode reports optional-backplane degradation without exposing endpoint, password or vendor exception details. `Trykatch.Realtime` exports `trykatch.realtime.outcomes` with closed `mode`/`outcome` dimensions only; IDs, cursors, policy keys and resource selectors are not metric dimensions.

## Module removal and retained data

Module registration is immutable for a running host. Disable dependent modules first through the existing CLI, then disable/remove the target, regenerate registries, deploy that composition, and drain/stop **every old API and worker** before declaring removal complete. For the supplied reference graph, Documents depends on Projects. A restarted host cannot authorize an absent policy/producer. Retain committed schema/history and paused durable work according to the existing module lifecycle; do not delete it merely to disable live push. There is no fleet-wide hot toggle, automatic history cleanup, session-retention job or new retention policy in this slice.

See [ADR 0018](../adr/0018-scheduler-cache-and-realtime-adapters.md), [frozen contracts](../specs/feature-parity-contracts.md#7-cache-and-realtime--s1s2) and [qualification evidence](../specs/evidence/parity-platform-services.md) for the distinction between dependency, application, process, proxy, browser and packaged-product checks.
