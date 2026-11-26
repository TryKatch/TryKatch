# ADR 0020: Transaction-aware organization mutation idempotency

Status: Accepted and implemented. S3 review, focused corrections and required exact-head CI are complete; [PR #136](https://github.com/TryKatch/TryKatch/pull/136) merged as `a3f19df`.
Date: 2026-10-24.

## Context

A client can lose a successful mutation response after PostgreSQL commits. Retrying a create with a new request identity can create another project, audit entry and outgoing intent. The existing atomic-response middleware already withholds success until the organization transaction commits. Mutation replay must join that transaction and preserve current authorization, cookie/antiforgery controls and explicit module composition.

Background job submission and organization creation already have their own recovery identities. This decision adds optional HTTP mutation enrollment without replacing those contracts.

## Decision

Use a completed-response receipt in PostgreSQL and an immediate transaction-scoped advisory lock. No new package, cache dependency or standalone service is required.

| Responsibility | Owning component |
| --- | --- |
| Validated policy, request/authority/resource/receipt records and store interface | Framework-free `Modules.Abstractions` |
| Explicit endpoint enrollment, bounded preparation before binding, endpoint filter, resource binding and response capture | `Modules.AspNetCore` |
| Current-context lock, fresh membership/permission checks and durable completed receipt | Host Infrastructure `OrganizationRequestStore` |
| Side-effect-free request and prereplay resource decisions | Owning module Application service, adapted by Presentation |
| Transaction lifetime, response staging, final commit/rollback and existing compensation | API transaction middleware and shared atomic-response feature |

Infrastructure does not reference Modules.AspNetCore. Domain/Application use cases do not receive HTTP capture or serialization responsibilities. The store neither invokes the use case nor commits the transaction.

### Identity, authority and reservation

Enrollment requires one installed module, a stable operation name, an organization mutation method and explicit organization permission policy, plus separate request and prereplay authorization callbacks. Anonymous, platform, safe-method and declared streaming/file enrollment is rejected. Projects enrolls Create, Update, Archive, Restore and Delete with an optional key; existing clients without a key retain their ordinary behavior.

The durable identity is `(OrganizationId, ActorId, ModuleId, OperationId, SHA256(key))`. The host derives organization and actor; neither comes from a caller-supplied receipt identity. The canonical intent includes protocol 1, method, escaped path/base, exact query, normalized JSON media type and canonical body or absent-body marker. JSON object ordering/whitespace and equivalent numbers normalize; arrays, path/query/media differences remain significant. Store hashes instead of separate original keys/request JSON; receipt bytes may still contain private submitted fields.

An immediate `pg_try_advisory_xact_lock` reserves a hash of that identity, excluding the request hash so changed intent competes for the same lock. Fresh membership/permission resolution and the module request gate follow the lock attempt, before any receipt, conflict or busy outcome is disclosed. The module prereplay gate checks current authority and the stored resource reference before returning response bytes. A 64-bit advisory-hash collision can cause an unnecessary busy response; the full database identity still prevents receipt sharing.

Contention returns 409 `idempotency_in_progress` with `Retry-After: 1`; changed unexpired intent returns 409 `idempotency_conflict`. A denied request does not learn whether a receipt exists. There is no durable unfinished row or lease requiring cleanup after a crash. A scoped store accepts one reservation attempt and completes its opaque token at most once in the exact same live, enlisted ReadCommitted application/control-plane transaction.

### Capture, commit and recovery

Idempotent HTTP preparation rejects object property names that collide under case-insensitive Web JSON binding, including nested objects and escaped names. Otherwise sorting `name` and `Name` could produce the same canonical fingerprint for different order-dependent bound values. This validation is confined to enrolled keyed HTTP requests; the shared canonical hash and existing outbox digest protocol remain unchanged.

Only newly reserved execution invokes the handler. The outer response feature serializes and flushes bounded staged bytes, captures them, inserts the completed receipt, and then commits. The application enlistment remains alive through this outer finalization; disposing it when the inner organization middleware returns would be too early.

Application `OnStarting` callbacks, including those registered before the transaction middleware, are staged and awaited once in reverse registration order before capture and finalization. A callback that changes the final status to non2xx triggers rollback; a callback failure also precedes commit. Receipt metadata is frozen through native publication. On replay, callbacks can produce fresh cookies/general headers, but the stored status/content type/Location and exact body remain authoritative. Native hosting callbacks outside application staging still run at the postcommit network boundary; they cannot change frozen receipt metadata through the response feature.

For enrolled execution, non-2xx results roll back business, audit, outgoing intent and receipt state before the error is published. Overflow, serialization, cancellation and explicit commit rejection also use the existing rollback/compensation path. Unenrolled endpoints retain their prior commit behavior. No transparent retry invokes a business handler after an uncertain commit.

The receipt contains only successful status, bounded content type, root-relative Location, exact bytes, optional opaque resource reference and completion/expiry. Replay copies those fields after current authorization and never restores old cookies or general headers. Files, streams, unbuffered/SSE results and nonempty bodies on 204/205 responses are rejected. Authors must exclude expiring/signed-URL operations; arbitrary JSON is not scanned for URL semantics.

Projects binds the server-created ID after create, or the route ID after other successful mutations. Its replay gate checks current `projects.manage` and org-bound existence, including archived/deletion-requested projects; it does not add a separate `projects.read` requirement or treat changed lifecycle/content as authorization loss. Missing/foreign resources deny replay.

### Bounds and storage access

Keys are 1–128 ASCII characters, initially alphanumeric and thereafter alphanumeric plus `._:-`. Requests are bounded UTF-8 JSON, at most 65,536 bytes. Replay lifetime is 1 minute–24 hours, default 24 hours. Response policy is 1–1,048,576 bytes, default 262,144 bytes, additionally clamped by outer staging. Content type is at most 256 characters and Location at most 2,048 characters. Validation rejects duplicate keys/JSON names, malformed JSON, excessive depth, unsupported encoding/media and oversized input before binding.

Application migration `20261024113623_AddMutationReceipts` creates the host-owned receipt table. FORCE RLS and exact SELECT/INSERT/UPDATE grants require both current organization and actor under the organization runtime role. The other four runtime profiles receive no receipt access; runtime DELETE is absent. Existing module SQL ownership and the five-role deployment boundary remain intact.

Effective replay expiry is the earlier of stored expiry and original completion plus the current endpoint policy. Expired identity replacement occurs only in the next successful enclosing transaction. After expiry, a key can execute again. There is no automatic purge, public receipt console or new business-retention policy; protect retained private response bytes using the existing database controls.

## Alternatives and consequences

- A cache-only receipt could disappear after a committed write; PostgreSQL retains the response with the effect and keeps cache availability out of correctness.
- A durable pending row/lease would need orphan recovery after process death; transaction locks release on rollback/connection loss and only completion persists.
- Waiting on a busy lock would hold HTTP connections; immediate bounded busy responses let callers retry explicitly.
- Re-running a use case to reconstruct a response could repeat side effects or change disclosure permissions; stored exact bytes plus separate module authorization avoid that coupling.
- Receipts consume private database storage and finite response memory. The bounds limit each operation, but expiry is not deletion and is not an indefinite exactly-once promise.

## Evidence and operation

[Platform-services evidence](../specs/evidence/parity-platform-services.md) records actual cookie/CSRF/five-role HTTP tests, write-first failure rollback, certified Production API process kills before/after commit, negative controls, and fresh renamed React/backend-only packed products. These are local qualification, not deployment HA or release proof. See the [contract ledger](../specs/feature-parity-contracts.md), [acceptance matrix](../specs/feature-parity-acceptance.md), and operations in [English](../operations/idempotency.md) / [French](../operations/idempotency.fr.md).
