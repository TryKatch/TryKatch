# Search, Indexing, and Caching Architecture

Status: Proposed for Trykatch v1
Date: 2026-09-08
Scope: clean-room architecture for tenant-safe querying, search, indexing, and caching

## Decision summary

Trykatch should adopt a deliberately small default stack:

- PostgreSQL remains the system of record and the v1 search engine.
- Module-owned query definitions use B-tree, composite, partial, GIN full-text, and `pg_trgm` indexes chosen from observed query shapes.
- Large or changing collections use cursor/keyset pagination with a fully unique ordering.
- Cross-module search is an organization-scoped PostgreSQL projection maintained through the transactional outbox.
- Application caching uses the .NET `HybridCache` API with in-process L1 caching and Valkey as the default optional distributed L2 provider.
- Authorization and organization resolution always happen before a cache lookup. Cache entries never substitute for permission checks or PostgreSQL RLS.
- Output caching is opt-in only for explicitly public, anonymous, stable endpoints. It is not enabled for authenticated organization APIs.

This gives Trykatch a deep, provider-neutral application interface without prematurely operating a separate search cluster. PostgreSQL supports the search shapes the template currently needs, while HybridCache provides Microsoft’s current two-level cache abstraction, serialization hooks, stampede protection within one application instance, and tag-based invalidation.[^dotnet-cache][^hybrid-cache]

## Repository evidence

The current template already has the right foundations:

- `Trykatch.AppHost` orchestrates PostgreSQL, the API, Vite, OpenTelemetry Collector, Loki, Tempo, Prometheus, and Grafana. It has no cache resource yet.
- The template uses .NET 10, EF Core 10, Npgsql 10, Aspire 13, PostgreSQL RLS, a transactional outbox, audit events, and separate platform/application contexts.
- Project, audit, organization, and platform-user lists currently use `ILIKE '%term%'`, `LongCountAsync`, and `Skip`/`Take` offset pagination.
- Existing indexes cover several equality and ordering paths, including organization membership, role names, audit occurrence time, and active project names. There are no trigram or full-text indexes.
- Organization isolation is already enforced at the application layer and with PostgreSQL RLS, including forced RLS and separate migration/runtime roles.

The gap is not a need for an external search product. It is a consistent query contract, indexes that match actual predicates and ordering, stable cursor pagination, a search projection seam, and safe cache semantics.

## Adopt now

### 1. A query kernel with module-owned contributions

Create a small `Trykatch.Search` kernel rather than a universal dynamic query engine. Each module contributes a declarative descriptor containing:

- stable module, entity type, and document type identifiers;
- allowed filter and sort keys mapped to typed expressions;
- the fully unique default ordering;
- searchable display fields and safe snippets;
- the permission required to discover and open a result;
- lifecycle/event mappings for create, update, archive, restore, and delete;
- a projection schema version and projector implementation.

This is a deep module: callers receive a compact, stable query/search interface while PostgreSQL, EF translation, cursor encoding, RLS, and index details stay behind it. Modules own business vocabulary and projections; the kernel owns validation, isolation, pagination, telemetry, and orchestration. A descriptor must fail startup/build validation when identifiers collide, a sort is not deterministic, a permission is unknown, or a projector is missing.

OpenMercato’s public module documentation similarly makes search a module contribution, and its public issue history contains a useful failure mode: a tenant token lookup performed a sequential scan because the index did not lead with the tenant and token columns used by the query.[^openmercato-module][^openmercato-index] Trykatch should adopt the contribution seam and query-shape validation, not copy implementation code.

### 2. PostgreSQL-native search baseline

Use the smallest index that supports a demonstrated query:

- B-tree for equality, range, and ordered retrieval.
- Composite B-tree with equality predicates first, then ordered cursor columns. PostgreSQL can use a multicolumn B-tree most effectively when leading columns are constrained.[^postgres-multicolumn]
- Partial indexes for stable lifecycle predicates such as active records. The query predicate must imply the partial-index predicate for PostgreSQL to use it.[^postgres-partial]
- GIN over a generated `tsvector` for natural-language full-text search. PostgreSQL recommends GIN as the preferred full-text index type, and Npgsql can map generated `tsvector` columns directly.[^postgres-fts-index][^npgsql-fts]
- `pg_trgm` GIN or GiST indexes for fuzzy matching and contains-style `LIKE`/`ILIKE`. A normal B-tree does not accelerate a leading-wildcard search, while `pg_trgm` can support indexed similarity and wildcard searches.[^postgres-index-types][^postgres-trgm]

Initial candidates, subject to `EXPLAIN` validation against production-shaped data:

```sql
CREATE INDEX ix_projects_org_active_created
ON app.projects (organization_id, created_at DESC, id DESC)
WHERE archived_at IS NULL AND deleted_at IS NULL;

CREATE INDEX ix_audit_org_occurred
ON platform.audit_events (organization_id, occurred_at DESC, id DESC);

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX ix_projects_name_trgm
ON app.projects USING gin (name gin_trgm_ops);
```

Do not create one partial index per organization. PostgreSQL explicitly identifies many non-overlapping partial indexes as a poor substitute for a leading category/tenant column or proper partitioning.[^postgres-partial]

Use `simple` text-search configuration as the neutral template default unless a module intentionally declares a language. Language-specific stemming is a product decision, not an infrastructure guess.

### 3. Cursor/keyset pagination

Add a standard cursor result contract for growing feeds and tables. Sort by a user-visible column plus a unique tie-breaker, normally `id`, and seek from the final tuple of the previous page. EF Core warns that ordering must be fully unique and recommends keyset pagination for next/previous navigation because offset pagination becomes increasingly expensive and can skip or duplicate rows during concurrent changes.[^ef-pagination]

Example ordering:

```text
(occurred_at DESC, id DESC)
(created_at DESC, id DESC)
(normalized_name ASC, id ASC)
```

Retain offset/page-number pagination only for bounded collections or interfaces that truly require random page jumps. Encode cursors as opaque, versioned, signed values so clients cannot inject sort expressions or cross organization boundaries.

### 4. Organization-scoped cross-module search projection

For global search, add one RLS-protected PostgreSQL projection, for example `platform.search_documents`:

```text
organization_id
module_id
entity_type
entity_id
title
summary
route
required_permission
lifecycle_status
search_vector
projection_version
source_updated_at
```

Use a unique key on `(organization_id, module_id, entity_type, entity_id)`, a GIN index on `search_vector`, organization-leading B-tree indexes for lifecycle and recency, and a trigram index on `title` only when fuzzy title matching is required.

Projection updates are eventual and outbox-driven after the source transaction commits. They must be idempotent. Provide a resumable reindex command scoped by module and organization with checkpointing and a projection-version rebuild path. Strongly consistent local list searches remain inside each module; the cross-module search UI must communicate that newly changed records can take a short time to appear.

Search returns discovery DTOs, not complete entities. Before returning or navigating to a result, re-check the current permission and rehydrate the record through its module. The projection contains no secrets, credentials, raw tokens, protected profile fields, or arbitrary JSON.

### 5. HybridCache with Valkey L2

Use `HybridCache` as the application caching seam. It combines an in-process L1 with an optional `IDistributedCache` L2 and protects a single application instance from cache stampedes.[^dotnet-cache][^hybrid-cache] Put a small Trykatch interface above it, such as `IReadModelCache`, that accepts a structured scope and a named policy rather than arbitrary string keys:

```text
CacheScope(platform | organization-id)
Module
Resource/read-model
Schema-and-policy version
Canonical parameter hash
Named TTL policy
```

Cache immutable DTO/read models only. Never cache tracked EF entities, authorization decisions, session state, antiforgery data, password-reset or invitation tokens, MFA state, security stamps, Data Protection keys, or mutable domain aggregates.

Use Valkey as the default distributed provider because it is BSD-3-Clause, governed as a Linux Foundation project, and has first-class Aspire hosting support while using the same StackExchange.Redis client integration.[^valkey-repo][^valkey-lf][^aspire-cache] The application stays provider-neutral through HybridCache:

```text
--cache none       HybridCache L1 only
--cache valkey     recommended/default distributed L2
--cache redis      optional licensed alternative
--cache garnet     optional Microsoft-stack alternative
```

`AddDistributedMemoryCache` is only for development/tests, not a distributed production topology.[^dotnet-cache]

### 6. Tenant-safe invalidation

A cache hit bypasses a database query and therefore bypasses that query’s RLS evaluation. Correct scoping is a security boundary:

1. Authenticate the actor and resolve the trusted organization context.
2. Authorize the operation.
3. Construct a structured cache key from the trusted organization ID, module, read model, schema/policy version, and a hash of canonical parameters.
4. Read or populate the cache.
5. Re-check authorization when returning security-sensitive resources; never treat possession of a cache key as authorization.

Never put a slug, email address, token, free-form search term, or raw user input directly into a cache key or telemetry. Microsoft warns that raw external input can cause unauthorized access and cache flooding.[^hybrid-cache]

Invalidate only after the database transaction commits:

- evict or tag-invalidate the current instance immediately;
- write a transactional outbox event in the same source transaction;
- let an idempotent worker publish organization/module/resource invalidations to other instances;
- keep L1 TTLs short enough to bound stale reads.

HybridCache tag invalidation is logical, and invalidating one server does not clear the other servers’ L1 entries; its stampede protection is also limited to one HybridCache instance.[^hybrid-cache] Therefore, security-critical values should bypass L1/caching or include a persisted version/security stamp in the key. Permission revocation must take effect from the authoritative store, not wait for TTL expiry.

Cache failure must degrade to the source database. The cache cannot be required for correctness, authorization, or accepting a write.

### 7. OutputCache only at an explicit public boundary

ASP.NET Core OutputCache should be opt-in per endpoint, not a global policy for Trykatch APIs. Its safe defaults avoid authenticated responses, responses that set cookies, and non-GET/HEAD requests. Place it after authentication and authorization as Microsoft documents.[^output-cache]

Do not output-cache login, logout, session, profile, CSRF, invite acceptance, password reset, MFA, organization resolution, permissions, health checks, or organization-scoped API responses. If a future public documentation/catalog endpoint is cached across nodes, use the dedicated StackExchange.Redis output-cache provider. Microsoft explicitly advises against backing OutputCache with generic `IDistributedCache` because the latter lacks the atomic operations needed for tagging.[^output-cache]

## Optional later

### Redis adapter

Redis can be offered as an adapter, but not Trykatch’s default. Redis versions through 7.2 use BSD-3-Clause; 7.4 through 7.8 use the Redis Source Available License v2 or SSPLv1; Redis 8 and later additionally offer AGPLv3.[^redis-licenses] Redis 8 can be self-hosted without a license fee under an applicable license, but that does not make infrastructure or operations free, and AGPLv3 creates source-distribution obligations that adopters must evaluate. Pin the exact image digest and record the selected license in the SBOM/release evidence.

### Microsoft Garnet adapter

Garnet is a promising MIT-licensed, .NET-based RESP server from Microsoft Research and is compatible with StackExchange.Redis for many workloads.[^garnet-repo] It is not a 100% Redis drop-in replacement; its compatibility documentation calls out semantic differences and missing modules, including non-atomic default behavior for some multi-key commands.[^garnet-compat] Add it only after Trykatch’s adapter contract, required-command suite, persistence, backup/restore, failover, and operational gates pass.

### pgvector semantic search

Use pgvector only when a generated application has an actual semantic-search or agent retrieval requirement and an explicit embedding model/provider. pgvector supports exact search plus HNSW and IVFFlat approximate indexes; filtered multitenant ANN queries require careful tuning because filtering occurs around approximate index scans and can affect recall.[^pgvector] Store organization ownership, permission metadata, source version, and deletion state with every vector. Keep it behind a search adapter so ordinary installations do not inherit embedding cost or data-governance obligations.

### OpenSearch adapter

OpenSearch is an Apache-2.0 search suite and is the preferred later external-engine option when measured PostgreSQL limits or product requirements justify it: complex relevance, high-volume faceting, language analyzers, large-scale log/search workloads, or independent search scaling.[^opensearch-faq] Treat it as a derived data store fed by the outbox, never a synchronous dual-write or source of truth. It adds backups, upgrades, security, deletion propagation, reindexing, and tenant-filter enforcement; it should not be in the default template.

## Avoid

- Do not cache every repository call or introduce a cache without a measured hot read path.
- Do not use a cache as an authorization store, session authority, durable queue, or source of truth.
- Do not expose Valkey, Redis, or Garnet to the public network.
- Do not cache organization-scoped data before resolving and authorizing the organization.
- Do not place PII, tokens, raw query strings, or permission sets in keys, tags, logs, or metrics.
- Do not continue leading-wildcard `ILIKE` on growing tables without a validated trigram/full-text strategy.
- Do not accept client-provided column names or SQL fragments; map known query keys to typed expressions.
- Do not rely on offset pagination for large or rapidly changing feeds.
- Do not create per-organization indexes or partitions by default.
- Do not synchronously dual-write the database and a search index; use the transactional outbox.
- Do not make Elasticsearch/OpenSearch, pgvector, Redis, or Garnet mandatory for a newly generated application.
- Do not treat HybridCache tags as immediate fleet-wide eviction.

## Security requirements

1. `search_documents` must have forced RLS and the same runtime/migrator ownership separation as other organization data. With RLS enabled and no matching policy, PostgreSQL defaults to deny; table owners normally bypass policies unless forced.[^postgres-rls]
2. Every query, index, projection key, cursor, cache key, invalidation event, and reindex checkpoint carries the immutable organization ID, not a user-provided slug.
3. Search results are filtered by current permission and lifecycle state. Counts and snippets must not leak inaccessible records.
4. Encrypted values are not copied into plaintext search projections. If equality lookup over protected values becomes necessary, design a separate keyed blind-index scheme and threat model.
5. Search input has normalized length limits, minimum fuzzy-search length, maximum page size, command timeout, cancellation, and rate limiting.
6. Dynamic filtering/sorting uses an allowlist. Cursor payloads are versioned, signed, expiry-bounded where appropriate, and rejected on organization/query mismatch.
7. Valkey/Redis/Garnet run on a private network with authentication/ACLs, TLS where traffic crosses a host boundary, non-root containers, secrets from mounted/environment providers, memory limits, eviction policy, health checks, backup policy where persistence is enabled, and no public port. Valkey’s own guidance assumes deployment inside a trusted network and recommends ACL/TLS hardening.[^valkey-security]
8. External indexes are server-only, encrypted in transit, permission-scoped, and included in retention, erasure, export, disaster-recovery, and incident-response procedures.

## Observability and index validation

Enable `pg_stat_statements` in development/performance environments to track normalized planning and execution statistics; access to query text must remain privileged because it can contain sensitive values.[^pg-stat-statements] Validate target query families with realistic organization cardinality using `EXPLAIN (ANALYZE, BUFFERS)`. `ANALYZE` executes the statement, so write statements must be tested inside a transaction that is rolled back.[^postgres-explain]

Capture:

- query family/operation ID, duration, returned rows, pagination strategy, and timeout count;
- search strategy (exact, trigram, full-text, semantic) and result count without raw search text;
- projection lag, outbox backlog, retries, poison events, and reindex progress;
- cache hit, miss, bypass, factory duration, failure, invalidation, payload-size bucket, and provider health without raw keys;
- database pool and command metrics through Npgsql’s OpenTelemetry-compatible metrics and tracing.[^npgsql-metrics][^npgsql-tracing]

Indexes are retained only when real workload evidence shows value. PostgreSQL recommends examining index usage and keeping statistics current rather than assuming an index helps.[^postgres-examine]

## Test and release gates

### Query and search

- Cross-organization tests prove rows, result counts, suggestions, snippets, and cursor continuation cannot leak.
- Permission revocation immediately removes discoverability and denies direct hydration.
- Keyset tests introduce concurrent inserts, updates, archives, and deletes and prove no duplicate or skipped surviving row across a traversal.
- Every target query family is tested on production-shaped multi-organization data with a documented latency/buffer budget and an acceptable plan.
- Trigram tests cover short terms, wildcard terms, similarity thresholds, accents/case, and fallback behavior.
- Full-text tests cover configuration, ranking ties, empty queries, and sanitized snippets.
- Projection tests cover out-of-order/duplicate events, archive/restore/delete, schema-version rebuild, resumable reindexing, and recovery from poison events.

### Cache

- The same logical request in two organizations cannot share a key or response.
- Permission changes, membership removal, organization suspension, archive, restore, update, and delete invalidate the right scopes.
- Multi-instance tests prove outbox invalidation and establish the maximum L1 stale-read window.
- Cache outage, timeout, eviction, and corrupt payload tests fall back safely without changing authorization or write correctness.
- Single-instance stampede tests prove one factory execution; distributed load tests document the remaining cross-instance behavior.
- Payload/key-size limits, serializer version changes, backward compatibility, and cold-start behavior are tested.
- Valkey is the required default adapter contract test; Redis and Garnet run the same suite when those options are shipped.

### Release

- Migration SQL is reviewed for concurrent production index creation where necessary; Npgsql supports PostgreSQL index methods, operator classes, included columns, and concurrent indexes.[^npgsql-indexes]
- Container images are digest-pinned, SBOMs record exact licenses, and license allowlisting distinguishes Redis version families.
- Vulnerability, secret, and configuration scanning cover the cache and optional search containers.
- Backup/restore and disaster recovery are demonstrated for every stateful provider that is marketed as production-ready.
- Generated templates build and test with `--cache none`, `--cache valkey`, and every shipped optional adapter.

## Phased implementation

### Phase 0 — Measure and specify

- Add stable operation/query IDs and PostgreSQL/Npgsql telemetry.
- Capture representative query plans and cardinalities for projects, audit, organizations, memberships, roles, and platform users.
- Define the typed query/filter/sort/cursor contract and performance budgets.

### Phase 1 — Repair current query paths

- Make every ordering fully unique.
- Introduce keyset pagination for growing tables and feeds.
- Add organization-leading composite/partial indexes and `pg_trgm` only where plans prove useful.
- Replace unrestricted contains-search patterns with a normalized, minimum-length search policy.

### Phase 2 — Add the search module

- Implement module search descriptors and validation.
- Create the RLS-protected `search_documents` projection.
- Feed it through the transactional outbox and add idempotent, resumable reindexing.
- Ship Projects as the reference module and add cross-organization isolation tests.

### Phase 3 — Add safe local caching

- Introduce `IReadModelCache` backed by HybridCache L1.
- Define named policies, key/version rules, telemetry, and explicit bypass rules.
- Cache only two or three measured, low-risk read models first.

### Phase 4 — Add Valkey L2 through Aspire

- Add the Aspire Valkey resource and StackExchange.Redis client integration, health checks, tracing, private-network configuration, memory/eviction limits, and failure fallback.[^aspire-cache]
- Implement transactional-outbox invalidation across multiple API instances.
- Run the isolation, outage, stale-window, and load gates before enabling it in production guidance.

### Phase 5 — Earn optional adapters

- Add Redis and Garnet only after the shared provider contract passes.
- Add pgvector only with a reference semantic-search module and retrieval evaluation set.
- Add OpenSearch only when measured requirements exceed PostgreSQL and its full operational lifecycle is documented and tested.

## Final recommendation

The production-oriented Trykatch default should be:

```text
PostgreSQL 18
  + organization-leading B-tree/partial indexes
  + pg_trgm for fuzzy/contains search
  + GIN tsvector for full-text search
  + RLS-protected outbox-fed search projection

.NET 10 HybridCache
  + L1 memory cache
  + Valkey L2 through Aspire for distributed deployments
  + transactional-outbox invalidation
```

This is free/open-source, Microsoft-aligned at the .NET integration boundary, tenant-safe when the requirements above are enforced, and replaceable at both the cache and search seams. It also keeps the generated application understandable: one authoritative database, one optional cache, and no external search cluster until evidence earns one.

## Primary sources

[^dotnet-cache]: Microsoft, [.NET caching](https://learn.microsoft.com/en-us/dotnet/core/extensions/caching).
[^hybrid-cache]: Microsoft, [HybridCache library in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0).
[^output-cache]: Microsoft, [Output caching middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0).
[^aspire-cache]: Microsoft, [Deploy Aspire caching integrations](https://learn.microsoft.com/en-us/dotnet/aspire/caching/caching-integrations-deployment).
[^postgres-multicolumn]: PostgreSQL, [Multicolumn indexes](https://www.postgresql.org/docs/current/indexes-multicolumn.html).
[^postgres-partial]: PostgreSQL, [Partial indexes](https://www.postgresql.org/docs/current/indexes-partial.html).
[^postgres-fts-index]: PostgreSQL, [Preferred index types for text search](https://www.postgresql.org/docs/current/textsearch-indexes.html).
[^postgres-index-types]: PostgreSQL, [Index types](https://www.postgresql.org/docs/current/indexes-types.html).
[^postgres-trgm]: PostgreSQL, [`pg_trgm`](https://www.postgresql.org/docs/current/pgtrgm.html).
[^postgres-rls]: PostgreSQL, [Row security policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html).
[^postgres-explain]: PostgreSQL, [`EXPLAIN`](https://www.postgresql.org/docs/current/sql-explain.html).
[^postgres-examine]: PostgreSQL, [Examining index usage](https://www.postgresql.org/docs/current/indexes-examine.html).
[^pg-stat-statements]: PostgreSQL, [`pg_stat_statements`](https://www.postgresql.org/docs/current/pgstatstatements.html).
[^npgsql-indexes]: Npgsql, [Indexes in the EF Core provider](https://www.npgsql.org/efcore/modeling/indexes.html).
[^npgsql-fts]: Npgsql, [Full-text search](https://www.npgsql.org/efcore/mapping/full-text-search.html).
[^npgsql-metrics]: Npgsql, [Metrics](https://www.npgsql.org/doc/diagnostics/metrics.html).
[^npgsql-tracing]: Npgsql, [Tracing](https://www.npgsql.org/doc/diagnostics/tracing.html).
[^ef-pagination]: Microsoft, [Pagination in EF Core](https://learn.microsoft.com/en-us/ef/core/querying/pagination).
[^redis-licenses]: Redis, [Licenses](https://redis.io/legal/licenses/).
[^valkey-repo]: Valkey, [Official repository](https://github.com/valkey-io/valkey).
[^valkey-lf]: Linux Foundation, [Launch of the open-source Valkey community](https://www.linuxfoundation.org/press/linux-foundation-launches-open-source-valkey-community).
[^valkey-security]: Valkey, [Security](https://valkey.io/docs/topics/security/).
[^garnet-repo]: Microsoft, [Garnet repository](https://github.com/microsoft/Garnet).
[^garnet-compat]: Microsoft, [Garnet compatibility](https://microsoft.github.io/garnet/docs/welcome/compatibility).
[^pgvector]: pgvector, [Official repository and indexing guidance](https://github.com/pgvector/pgvector).
[^opensearch-faq]: OpenSearch, [FAQ and licensing](https://opensearch.org/faq/).
[^openmercato-module]: OpenMercato, [Module development](https://github.com/open-mercato/open-mercato/blob/main/.ai/docs/module-development.md).
[^openmercato-index]: OpenMercato, [Issue #2966: tenant token query sequential scan](https://github.com/open-mercato/open-mercato/issues/2966).
