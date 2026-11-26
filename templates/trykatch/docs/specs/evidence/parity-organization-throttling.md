# Distributed organization request admission evidence

The host's closed `organization-api` policy admits attempts for the trusted current organization after endpoint authorization and cookie request verification, before feature, idempotency and handler effects. Earlier actor/IP/authentication/assistant controls remain in place. Request admission is separate from logical storage quotas and is disabled by default.

## Implemented boundary

`OrganizationRequestLimits` is captured once at startup. Opt-in defaults are 300 permits per 60 seconds, with validated ranges 1–1,000,000 and 1–3,600 seconds. Unknown properties, malformed values and out-of-range values fail startup even when disabled. The parser accepts the host's flattened empty parent with actual child properties, matching the existing quota parser's object handling.

`app.organization_request_limits` retains one current-window row per organization and the fixed policy. Its tenant-first primary key, forced RLS and exact organization SELECT/INSERT/UPDATE policies are included in the forward application migration, host ownership map, runtime provisioning and live inspector contract. No runtime DELETE or other-profile access is added. Window timestamps come from PostgreSQL. The first admitted request starts a window; the first request after its end resets the count through an atomic conditional upsert.

Each admission uses a separate organization-profile ReadCommitted transaction with trusted organization/actor GUCs. Its connection is the same organization profile captured by the host at registration, so configuration reload cannot redirect counters away from the database used for authorization. It commits before business handling. Parent rollback, feature rejection and receipt replay do not refund a permit. Exhausted attempts do not increment the row. The same row lock protects admission and the authoritative database-time Retry-After observation. The operation has a four-second deadline, two-second statement timeout, 500 ms lock timeout and three-second command timeout. Exhaustion returns 429; unavailable coordination returns a sanitized 503. There is no cache or per-process allowance fallback.

The enabled middleware explicitly checks unsafe application-cookie antiforgery before admission. `UseAntiforgery` can record a failure without enforcing it; existing endpoint filters execute later. The middleware reuses recorded validation or the registered antiforgery service. Disabled admission adds no earlier verification. Untrusted organization headers are not a target selector.

## Qualified evidence

All successful selected runs below have zero skips.

| Evidence | Scope |
| --- | --- |
| `request-limit-settings-schema-green` | 41 unit cases: configuration bounds and flattened host object regression, actual EF tenant-first model/filter, exact profile mapping, existing operational ownership, affected quota startup fixtures and maintenance health. |
| `request-admission-final-green` | 18 real PostgreSQL cases: ten admission cases (including captured-profile reload protection), one exact live-profile/RLS case and seven HTTP cases using the production API pipeline. |
| `request-limit-architecture` / `request-limit-doctor` | Two host project-boundary/scanning checks pass; the normal module doctor reports a healthy workspace. |
| `request-limit-affected-boundaries` / `request-limit-affected-diagnostics` | Eight distinct existing security cases, with the successful three-case diagnostic supplement replacing the initial failing rows: inspector/profile/operational ownership, module antiforgery, canonical replay, revoked-management replay denial and actual assistant scope/antiforgery/RLS/read-only/rate limits. |

Admission cases prove concurrent independent callers share the final slots, organizations are independent, rollback retains the committed attempt, rollover follows database time, the disabled default requires neither a target nor database, unresolved targets fail closed, caller cancellation does not consume capacity, database outage retains the count, and row-lock contention is bounded without local fallback. The live-profile case checks forced RLS and every table privilege across all five runtime roles, hides the other organization's row and rejects DELETE.

HTTP cases use independent API service providers against the same PostgreSQL database. Two providers share one allowance and ignore a forged organization header. An explicit read-only role's denied mutation and an unverified cookie mutation consume no permits. Two identical requests replay one set of project/audit/receipt effects while consuming two permits; a third attempt gets 429. Handler validation and feature rejection still consume permits. A locked counter returns a sanitized bounded 503, retains its count and recovers after release. An actual unavailable Redis backend is independently observed through its degraded read probe; PostgreSQL admission still enforces exhaustion.

## Failures investigated and limits

The unavailable scaffold produces eight direct semantic failures and one passing unresolved-target denial. Corrected HTTP fixtures select the workspace after login and explicitly assign a role without project management. Without middleware, they expose missing accounting and a 200 response where exhaustion requires 429. The first implementation then exposes the flattened parent configuration regression; a narrow unit reproduction fails before the parser correction. The selected 17-case acceptance run passes afterward. A further real configuration-reload regression exposes dynamic connection lookup after authorization; the production factory now captures the same organization connection as the existing host contexts. The new test restores its mutated chained configuration in finally to avoid leaking test state. The final selected 18-case run passes with zero skips.

Existing standalone quota startup fixtures needed the explicit OpenAPI-generation flag and logging after worker/health registration changed; their relevant checks now pass. The hand-built inspector fixture needed the new forward migration and exact counter grants; production provisioning was already qualified by the new live-role test. One existing antiforgery scenario's second classified upload initially returns 500. A response-body diagnostic was added, and the targeted rerun passes without a production change. No causal fix is claimed for that transient result; it remains an observation for the final S5 validation/review.

Safe usage inspection/UI components are qualified in [usage evidence](parity-quota-usage.md). Freshly packed renamed products, separate-process replicas, composed RustFS storage/restart journeys and actual generated-app usage UI, the single integrated review, exact-head CI and merge remain Task8 requirements. S6, phases3–11 and the final main release remain open. These local results do not establish the entire S5 or release boundary.
