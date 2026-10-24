# ADR 0018: Scheduler, cache and realtime adapters

Status: Accepted architecture choice for parity implementation. Candidate versions below are research observations, not installed or fully qualified dependencies.
Date: 2026-10-23.

## Context

TryKatch already owns outbox/audit workers, S3-compatible storage, SMTP and a redistributable dependency policy. General scheduling, distributed read-model caching and cross-replica realtime are additional capabilities. Their libraries must not appear in module contracts or turn a cache/backplane into the business ledger or authorization source.

The existing repository research on search/caching recommends HybridCache over a scoped read-model interface and Valkey L2. Reuse that direction rather than introducing a second cache abstraction or selecting Redis licenses implicitly.

## Decision

| Capability | Selected implementation boundary | Observed candidate / license |
| --- | --- | --- |
| Durable job/recurring trigger adapter | Quartz.NET 4 with PostgreSQL ADO store behind `IModuleJobScheduler`; one registered bridge job asks the host executor to run persisted work. | [Quartz 4.6.0](https://www.nuget.org/packages/Quartz/4.6.0), .NET 10 target, Apache-2.0. Existing Npgsql is the database driver; dependency compatibility is checked when E4 resolves the graph. |
| Read-model cache | `IOrganizationCache` backed by HybridCache, optional Valkey L2 through the Microsoft StackExchangeRedis cache adapter. | [HybridCache 10.11.0](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Hybrid/10.11.0), [cache adapter 10.0.11](https://www.nuget.org/packages/Microsoft.Extensions.Caching.StackExchangeRedis/10.0.11), MIT package metadata. |
| First-party realtime | ASP.NET Core SignalR with existing same-origin cookie identity; optional StackExchangeRedis backplane using Valkey in a supported same-region topology. | [SignalR adapter 10.0.11](https://www.nuget.org/packages/Microsoft.AspNetCore.SignalR.StackExchangeRedis/10.0.11), MIT. [StackExchange.Redis 3.4.20](https://www.nuget.org/packages/StackExchange.Redis/3.4.20), MIT, is an observed candidate, not a mandatory transitive override. |
| Distributed server | Valkey, separate private infrastructure, not an application-domain store. | [Valkey 9.1.2](https://valkey.io/) observed stable server candidate, BSD-3-Clause; pin an exact vetted image digest during implementation, not a floating tag. |

No package, image, lockfile, dependency version or production configuration is changed in Phase 0. The precise mutually compatible versions are pinned together at implementation after resolved transitive vulnerability/license audit and isolated tests. Published direct-package metadata is insufficient to claim a clean resolved graph or certify compatibility/performance.

### Why these choices

Quartz provides persisted/clustered scheduling and PostgreSQL support without building another general scheduler from the existing outbox loop. Its [Apache-2.0 license](https://github.com/quartznet/quartznet/blob/main/license.txt) is on the current allowlist. Quartz 4's core package includes hosting/DI/System.Text.Json integration; do not blindly add Quartz 3-era integration packages to a Quartz 4 implementation. Keep schema provisioning in the migrator, using the exact pinned vendor schema and reviewed host grants; [Quartz job-store guidance](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html) distinguishes production schema application from runtime creation.

Hangfire remains a functional benchmark, not the selected implementation dependency. Its [published Core license](https://www.nuget.org/packages/Hangfire.Core/1.8.25) is LGPL-3.0-or-later, outside TryKatch's current approved SPDX list. No exception or commercial license is assumed. A custom persisted scheduler was considered but would add general scheduling/cluster/misfire code that the host should not maintain without a demonstrated requirement.

HybridCache provides local stampede protection and L1/L2 integration. [Microsoft's guidance](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0) makes clear that instance-level invalidation/stampede behavior is not a cross-replica consistency guarantee. Keys include host-derived organization/actor scope and policy/version hashes; permissions, sessions, security stamps, tokens/keys and mutable EF objects stay out of the cache. Preserve current authorization before returning cached DTOs.

Valkey is the default selected distributed implementation; do not equate a compatible RESP client with approval of every Redis/server license/version. Its integration must pass the required-command, invalidation, failure, backup/restart and image-license checks. Shared-server application/environment prefixes and private network access are mandatory.

SignalR is a delivery optimization, not a durable feed. [Microsoft's backplane guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0) states that messages sent during a server outage can be lost and recommends low-latency co-location; no buffering guarantee is assumed. Durable history, cursor reconciliation, current audience authorization and independently committed outgoing intent cover this boundary.

### Supported absence and failure

- No general jobs enabled: existing applications/outbox/audit workers continue as before; no new mandatory scheduler connection/service is imposed until the capability is selected.
- Jobs enabled without a valid durable PostgreSQL profile/schema: readiness fails explicitly. Never downgrade production jobs to memory or execute with a privileged fallback credential.
- Cache absent/down: authoritative read path is used. L1-only may be an explicit configured read optimization; it never supplies permission, session, usage ledger or quota correctness.
- Realtime disabled: no live-delivery promise; HTTP/history/inbox continues. A selected multi-replica realtime deployment needs the configured backplane; outage degrades live updates and reconciliation recovers durable state. Never silently claim cross-node delivery with a single-process fallback.
- Valkey absent/down: cache falls back to source reads; live push degrades as above; PostgreSQL idempotency, hard quota reservation, usage ledger and jobs remain authoritative. No hard-limit decision becomes fail-open because a cache is unavailable.

## Implementation gates

E4 owns Quartz adapter/schema/role/bridge tests; S1 owns cache isolation/invalidation; S2 owns cookie/Origin/audience/backplane/reconnect proof. D5 architecture choice is resolved by this ADR; version resolution, vulnerability review, license allowlisting, package/image pinning and runtime qualification remain required acceptance gates, not undecided library choices.

### E4 implementation status

The TryKatch infrastructure source now pins Quartz core 4.6.0 and uses the existing Npgsql 10.0.3 provider. The adapter is opt-in through `Jobs:Enabled`; OpenAPI generation does not register its hosted scheduler. It uses the fixed `trykatch_work_coordinator` connection, PostgreSQL ADO clustered store, `infrastructure.qrtz_` prefix, JSON serialization, string job data, and schema validation without runtime DDL. Application migration `20261023221455_AddDurableModuleJobs` creates the 12 Quartz base tables and indexes, excludes optional execution history, and applies FORCE RLS policies scoped to the coordinator role. The adapted DDL preserves the upstream [Quartz 4.6.0 PostgreSQL schema](https://github.com/quartznet/quartznet/blob/v4.6.0/database/tables/tables_postgres.sql), licensed Apache-2.0; the migration records its source and SHA-256.

The implementation source also defines a fixed work bridge, explicit host recurring-target bindings, a durable TryKatch work ledger, bounded schedule reconciliation, and permission-gated work controls. The work ledger remains authoritative; Quartz carries only a persisted work ID or a registered target reference. Jobs remain disabled unless explicitly enabled, with no in-memory scheduler fallback. Recurring `StartAtUtc` is inclusive; scans are capped at 100,000 and cursor advancement commits atomically with occurrence insertion. Before a new business attempt, the executor checks current authorization after completion-marker/recovery handling. Shutdown requests cooperative interruption and waits for active Quartz work within the host deadline.

Local implementation qualification is recorded in the [E4 evidence](../specs/evidence/parity-execution-e4.md). The isolated Quartz/Npgsql dependency graph audit is clean and resolved licenses are permitted; the merge-gate follow-up patches the previously recorded Pkcs, Xml and MessagePack advisories and passes normal locked restore plus the full solution package audit. E4 application migration, runtime-role inspection, build, focused PostgreSQL tests, restart/shutdown checks and integrated review passed locally. Do not treat source inspection or the isolated audit as release qualification. Apply the forward migrator and inspect exact coordinator grants before enabling jobs on existing database volumes. E5 queued email remains outside this implementation.

Do not expose Quartz's HTTP API/dashboard or serialized CLR job invocation automatically. The existing planned platform work UI controls only registered host operations under platform permissions. Do not introduce Redis, Garnet, a broker, external search, standalone SSE or a managed cloud realtime service as implicit extra dependencies.

## Evidence scope

Official package/docs/license sources were reviewed on 2026-10-23. Direct NuGet metadata observations and their limitations are recorded in [Phase 0 evidence](../specs/evidence/feature-parity-phase-zero.md). No dependency was restored, security-audited as a resolved graph, built or exercised in this phase.

## S1 cache implementation qualification

The cache adapter now has local qualification in [platform-services evidence](../specs/evidence/parity-platform-services.md): the actual application HybridCache/StackExchangeRedis graph and permitted licenses, authenticated pinned Valkey, durable organization-RLS generations, safe read-only scope admission, current Projects authority, HTTP commit/rollback, two independent cache processes, outage/recovery, optional private topology and packaged renamed outputs. It deliberately performs a committed PostgreSQL generation lookup on each eligible hit. Default reads remain Disabled; invalidation generations persist independently of read mode or cache availability. This does not qualify SignalR/backplane behavior or production HA failover. Integrated implementation review and exact-head PR gates remain pending.
