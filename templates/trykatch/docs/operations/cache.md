# Optional organization read cache

Trykatch can use an optional organization-scoped cache for eligible immutable read DTOs. PostgreSQL remains authoritative: authorization and the committed invalidation generation are checked on every cache hit. A cache miss, disabled cache, unsupported transaction scope, or cache outage reads from the source. The generation query means a cache hit still performs one indexed PostgreSQL query; the cache avoids rebuilding the DTO, not all database access.

The default mode is `Disabled`. Set `Cache:Mode` to `LocalOnly` for a per-process cache, or `Distributed` for a shared cache. Cache mode is runtime configuration, independent of CLI switches and installed modules. Writes continue to record durable invalidation generations while reads are disabled, so disabling, writing, and re-enabling cannot revive an older value. Do not reset or purge generation rows while cache entries might remain.

## AppHost development

Set `Cache:Mode` to `Distributed` in AppHost configuration (for example, its user-secrets store) to add the pinned Valkey 9.1.2 container. AppHost requests the `cache-password` Aspire secret parameter and passes an authenticated StackExchange.Redis connection to the API. Keep that password in the local secret store. Leave `Cache:Mode` unset or set it to `Disabled` to omit the cache resource. AppHost's local endpoint is for development; it does not claim production availability or transport encryption.

## Compose

The cache is an opt-in overlay, compatible with either regular or backend-only Compose. Add `TRYKATCH_CACHE_PASSWORD` to the protected `.env` file using a long random URL-safe value. Keep it out of source control and shell output. The same value authenticates API and Valkey.

For the regular application, use:

```sh
docker compose --env-file .env -f compose.yml -f compose.cache.yml up -d --build
```

For a backend-only application, use:

```sh
docker compose --env-file .env -f compose.backend.yml -f compose.cache.yml up -d --build
```

Without `compose.cache.yml`, the API remains in its default disabled mode and no Valkey service is started. The overlay's Valkey service joins only an internal Docker network, publishes no host port, has no durable volume, requires a password, disables persistence, and evicts least-recently-used entries at a 64 MiB memory limit. The API retains its normal network for PostgreSQL and other services.

For an externally managed distributed cache, set `Cache__Mode=Distributed` and `ConnectionStrings__cache` in the API runtime environment. The connection value must contain exactly one endpoint and a password, using StackExchange.Redis syntax, for example `cache.internal:6379,password=<secret>`. Load credentials through the deployment's secret store or protected environment and keep literal values out of documentation, logs, and support bundles. Configure the cache with authentication and private network access. This template does not claim encryption in transit; use TLS for links that cross a trusted private network boundary or where deployment policy requires it. The Compose overlay's local Valkey service is not a production high-availability service.

## TTL and namespace

The host defaults are 30 seconds for `short` and 120 seconds for `standard`. In Compose, set `TRYKATCH_CACHE_TTL_SHORT` and `TRYKATCH_CACHE_TTL_STANDARD`; in other deployments, set `Cache__TtlSeconds__short` and `Cache__TtlSeconds__standard`. Values must be from 1 through 600 seconds. Keep TTLs short for data whose freshness matters; PostgreSQL generation checks remain required regardless of TTL.

The default application namespace is derived from the shared infrastructure assembly, so it is stable across API and worker executable names. To override it in Compose, add `Cache__ApplicationNamespace` to the API's environment in a deployment-specific Compose override; set the same value on any separate worker service. In AppHost or other deployments, set `Cache:ApplicationNamespace` or `Cache__ApplicationNamespace`. When multiple products or environments share a distributed cache, use the same value across all processes belonging to one product and distinct values for each environment/product boundary. Use a bounded lower-kebab name, not a secret or customer value. Changing namespace makes prior entries unreachable; it does not reset or remove durable PostgreSQL generation rows.

The cache is an optimization only. Do not place authorization, membership, permission, token, session, quota, or other authority decisions in it. The source remains the fallback during a cache outage, and source failures remain source failures.

## Diagnostics

The cache contributes to API readiness at `/health/ready` under the `organization-cache` check. In `Disabled` and `LocalOnly` modes the check is healthy without contacting a cache backend. In `Distributed` mode it performs a fixed read-only connectivity probe with a two-second deadline. An unavailable optional cache reports a degraded readiness check; eligible reads continue through the authoritative source. The response exposes only the health status, not cache keys, endpoints, credentials, vendor errors, or a custom cache-specific JSON payload. The probe does not create cache entries or update invalidation generations.

The `Trykatch.OrganizationCache` meter reports these counters:

| Instrument | Meaning |
| --- | --- |
| `trykatch.cache.reads` | `eligible` reads use the configured cache after PostgreSQL generation admission; `bypassed` reads go directly to the source. |
| `trykatch.cache.sources` | A source factory started. This includes cache misses and bypasses; it is not a cache hit/miss counter. |
| `trykatch.cache.invalidations` | `staged` records a durable invalidation intent in the current transaction. It does not mean the transaction has committed. |
| `trykatch.cache.refusals` | A shared value was refused for `dirty`, `oversized`, or `corrupt` state/payload conditions. |

All cache metric dimensions are limited to `mode` (`disabled`, `local`, or `distributed`) and `outcome`. The telemetry does not include organization, actor, module, read-model, key, DTO, endpoint, or credential values. Do not add those values to dashboards, logs, or support bundles.

For a degraded Distributed readiness check, confirm that the API has one private cache endpoint and that its password is loaded from the deployment's secret store. Check network reachability and the cache service's authenticated health without printing connection strings or credentials. The application falls back to source reads when the optional cache is unavailable; PostgreSQL and source errors still need to be investigated as source failures.

An eligible cache read always checks the committed PostgreSQL generation first, so expect one indexed database lookup per eligible hit. Invalidation does not reset generation rows or synchronously contact every process; peers observe the new generation on their next read, and old physical entries expire by TTL. Keep configured TTLs between 1 and 600 seconds. The default factory timeout is 5 seconds (allowed range 1–30 seconds); it is cooperative, and scoped source work is not abandoned on caller cancellation. Cache entries above 65,536 serialized bytes are not stored.

## Existing database rollout

Apply the forward application migration `20261024043449_AddOrganizationCacheInvalidations` with the existing migrator before upgrading any API or worker, including deployments that keep cache reads Disabled. Project writes always record durable generations. The migrator provisions the reserved relation, FORCE RLS policies and exact organization-role SELECT/INSERT/UPDATE grants; inspect them using the existing [database isolation workflow](../module-data-isolation.md). No cache credential or running Valkey service is required for this migration.
