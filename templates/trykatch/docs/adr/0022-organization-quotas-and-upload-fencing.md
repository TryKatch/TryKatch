# ADR 0022: Organization quotas and provisional upload fencing

Status: Architecture decision for S5; production implementation and composed qualification pending. Reconcile against merged S4 before implementation.
Date: 2026-10-24.

## Context

The frozen parity contracts require module-neutral entitlement reading, reserve/commit/release, immutable usage, abandoned-work reconciliation and organization request throttling. PostgreSQL already supplies trusted organization transactions and five explicit runtime profiles. Documents writes object bytes before its metadata transaction commits; an in-memory rollback callback cannot recover a process crash or indeterminate outcome.

The existing storage interface has PUT/GET/DELETE only. Six local tests against the pinned RustFS1.0.3 image establish conditional create/replacement, exact HEAD metadata/length, independent byte/hash reads, restart and actual successful-response loss. They also show that versioned storage retains noncurrent payload bytes after a zero-byte replacement. These are primitive qualifications, not completed quota or crash-safety proofs.

## Decisions

### Resource meaning and current authority

An enabled module explicitly declares its stable owner-prefixed resource key and unit. Quota resources, installed modules, user permissions and availability flags remain separate. A quota result grants none of the other three. Unknown/absent resources cannot allocate; retained operational history does not execute absent module code.

The Documents resource is `documents.storage-bytes`, measured in logical uploaded document bytes. Active, Archived and pending-deletion documents count equally. Outstanding admitted uploads count as holds. Provider noncurrent versions, replication, metadata and temporary-file overhead are outside this metric. S5 does not promise a bound on provider physical occupancy or introduce a purge policy. This interpretation follows the existing business-usage contract; it corrects the earlier preparation's physical-byte assumption.

Quota configuration defaults to Disabled. Explicit finite and explicit Unlimited are distinct from an enforced missing/unconfigured limit. Disabled preserves usage/admission identity without applying a ceiling; it does not let an older replica ignore a newer durable enforced projection. Infrastructure uncertainty is unavailable, never unlimited.

### Publication and capacity

Host configuration is the initial entitlement source. Publish its immutable revision/hash through a compiled host authority and the separate platform profile in a validated organization target scope. The organization runtime reads the safe projection and cannot edit its allowance. Equal revision/different content fails closed; older configuration cannot overwrite newer durable state. B1 later supplies a reviewed publisher through the host seam without a kernel reference to Billing implementation. No S5 HTTP endpoint accepts arbitrary allowance values, and no commercial defaults are selected.

Projection publication, reservation, completion, release, usage changes and reconciliation share one organization/resource serialization key. Apply the current configuration batch before acquiring the caller's resource locks; do not open a child transaction that waits on a lock already held by its parent. Under the lock, finite admission checks committed usage plus all outstanding holds plus requested units against the current limit, with checked Int64 arithmetic. A lower limit preserves existing holds and data, marks over-capacity state and denies new allocation.

Reserve is bound to current actor/resource/operation/units. An identical active retry is AlreadyReserved; conflicting identity/units and terminal retries conflict without revealing the previous record. Commit of an already committed reservation and Release of an already released reservation are no-ops. Release cannot refund committed usage; a confirmed permanent removal is a separate immutable, resource-owner-verified effect. Archive and delete-request transitions never refund. Ledger effect identity prevents duplicated delivery or inventory/live-charge races from counting twice.

### Durable upload admission and finalization

An explicit installed upload-policy reference binds the owning resource and required permission. The HTTP client cannot choose an actor, organization, permission, object key or authority frame. Bind actor, original membership, operation ID, stable subject ID/key, actual byte length, SHA-256, normalized metadata fingerprint and adapter-derived storage namespace identity. The first-party UI retains its operation UUID across an uncertain retry; omitted legacy UUIDs receive a fresh server operation.

Admission creates a new DI scope and organization transaction, enlists its contexts, re-resolves current access, requires the original actor/membership and owning permission, validates supported placement and initializes that scope from the fresh result. Reservation, upload operation and reference-only maintenance work commit together before returning permission to perform PUT. Setting SQL organization/actor variables alone is not authorization, and this transaction must not reuse the parent's scope.

Finalization stays in the original request's enlisted transaction. Revalidate current authority and operation after acquiring the resource lock; document metadata, audit, outbox, positive usage and reservation/upload completion commit atomically. A concurrent completed retry skips the business callback and permits the owning module to load only its currently authorized subject. A terminal cancelled operation never starts another upload. Register technical rollback compensation before PUT; run it only after confirmed rollback releases the outer lock. An indeterminate commit is resolved from durable state rather than destructively compensated.

### Permanent cancellation fence

Initial object publication is atomic create-if-absent at a key that is never reused. Actual streamed length/hash must match the admitted request before a write can be treated as verified. A repeated matching object is recoverable; different, malformed or unowned identity is not overwritten.

Expiry/technical cancellation first commits Fencing under the same resource lock that finalization holds through outer commit. This prevents future metadata completion. Outside that transaction, atomically create a recognizable zero-byte cancellation sentinel if absent, or replace only the exact operation-owned provisional object using its current opaque conditional revision. Confirm the sentinel, then release the uncommitted logical hold in a fresh trusted transaction. A losing conditional request must reread/retry; timeout or unknown state retains the hold and reports reconciliation work.

Never delete the sentinel or reuse its key: a late initial writer must continue to lose create-if-absent. External lifecycle deletion of sentinels is incompatible with the qualified protocol. A storage namespace mismatch cannot silently target another endpoint, bucket or root. Versioned provider copies may remain physically stored; S5 neither purges nor bills those copies as committed document usage.

The S3 adapter uses the qualified conditional operations and HEAD metadata without assuming ETag equals SHA-256. The local adapter must qualify atomic no-overwrite publication and cross-process compare/replacement with atomically consistent identity/data. A stat followed by ordinary overwrite, or a rename alone, is insufficient. Local multi-process support requires a coherent qualified filesystem/root; independent replica disks are not a shared object store.

### Inventory and bounded maintenance

The owning module supplies a registered inventory adapter; the kernel does not query Documents entities or tables. Inventory includes uploaded rows across all lifecycle states, excludes metadata-only legacy rows, and fails closed on unresolved uploaded sizes. Batch/checkpoint processing and live charging use the same logical effect identities. Finite admission waits for complete inventory. Usage inspection reports initialization/unavailability rather than a partial total as complete. Deploy quota-aware writers and complete initial inventory before enabling finite enforcement; mixed older software writers are outside that qualification.

Use `infrastructure.quota_maintenance_work` for closed reservation/upload-operation/resource references and bounded lease/scheduling state. A closed work-kind distinguishes transaction-only hold expiry, provisional-operation cancellation and registered resource inventory; it cannot select an arbitrary handler. Public Reserve is transaction-only; external object I/O must use composite durable upload admission. An immutable effect kind prevents missing/corrupt upload state from being mistaken for safe storage-free expiry. The queue contains no storage key, customer payload, permission set, actor override or handler name. The organization runtime inserts intent; the existing coordinator claims/acknowledges with SELECT/UPDATE only. It gains no business/usage grants and no new runtime profile is added.

A fixed internal host executor validates its claimed reference against durable organization state before establishing an empty-permission maintenance principal in a fresh transaction. It may invoke the registered inventory read seam or the exact provisional-operation fencing/release transitions; it cannot invoke ordinary Documents management. Original actor identity is provenance only. This is a deliberate host authority seam, not a fabricated installed module/system grant or a public scope initializer. Test its denials explicitly.

Follow ADR0015's active-organization/shared-placement eligibility. Inactive/missing organizations or unsupported placement pause with holds retained and resume when eligible. The host may cancel a previously admitted absent-module provisional operation from retained host state without loading module code; it never invokes an absent inventory/business handler. Storage/coordination uncertainty retries with bounded batches/backoff and visible safe health state. A five-attempt terminal cutoff must not silently abandon a capacity hold.

### Exact data profiles and request throttling

Forward migrations, ownership declarations and live inspector expectations change together. All organization relations use forced RLS and tenant-first uniqueness. Quota usage and publication history are append-only; no runtime DELETE or blanket host-table CRUD is implied.

| Relation | Exact runtime access |
| --- | --- |
| `app.organization_entitlements` | Organization SELECT; validated host publisher through platform SELECT/INSERT/UPDATE in target scope. |
| `app.organization_quota_reservations`, `app.module_upload_operations`, `app.organization_quota_inventory` | Organization SELECT/INSERT/UPDATE for bounded host transitions; no DELETE. |
| `app.organization_usage` | Organization SELECT/INSERT; no UPDATE/DELETE. |
| `platform.entitlement_publication_history` | Platform SELECT/INSERT; no organization access or history mutation. |
| `infrastructure.quota_maintenance_work` | Organization INSERT; coordinator SELECT/UPDATE. |
| `app.organization_request_limits` | Organization SELECT/INSERT/UPDATE of one bounded current-window row per closed policy. |

Identity and outbox receive none of these permissions; the coordinator receives only its queue access. A publication connection is never replaced with organization or migrator credentials. Ordinary module-neutral quota operations require a current enlisted scope; system callers additionally require a finite explicit allowed-quota-resource grant.

Add separate PostgreSQL request admission after trusted organization resolution/authorization and antiforgery, before feature/handler/replay effects. Preserve existing actor/IP/auth/assistant controls. Use database time and an independent short transaction; admitted attempts count even if business work rolls back. Return429 and Retry-After for exhaustion,503 for unavailable coordination. No per-replica fallback supplies a false distributed guarantee. Default disabled; explicitly enabled default is300 attempts per60seconds, with validated finite bounds.

### User and deployment surfaces

The existing Organization Settings surface reads safe current-organization usage under `organizations.read/manage`, with Int64-safe values, mode/source/version, committed/reserved/reconciliation-held totals and explicit initializing/unavailable/paused states. It cannot edit its allowance and exposes no object keys or raw actor provenance. No new assistant write tool is added.

Qualify real PostgreSQL/profile behavior, real storage, crashes/restarts and two API replicas; then fresh renamed React/backend-only packages, actual browser UI, module absence/restoration, the single integrated review and final-head CI. Primitive tests, mock stories and source assertions alone do not establish those guarantees. No live deployment, provider migration, commercial policy or main release is performed by this ADR.

## Alternatives and consequences

Cache-authoritative counters, an in-memory limiter and post-rollback-only reservations fail replica/crash guarantees. Timer-only release, delete-then-retry and reusable keys permit late writers after capacity release. Permanent holds without reconciliation do not complete abandoned-work expiry. Giving a coordinator business SQL or fabricating a module grant crosses the existing profile/authority boundary. Raw platform allowance editing conflicts with the no-Billing host-configuration source. These alternatives are rejected.

The chosen design adds durable operational state and an explicit technical maintenance authority. PostgreSQL/storage failure can delay uploads or retain holds rather than oversubscribe or report an uncommitted charge. Permanent sentinels and provider versions carry physical overhead outside the logical-byte resource. Those operational costs and later D3 disposal remain explicit; no customer-data deletion is inferred from a quota downgrade.

See [primitive evidence](../specs/evidence/parity-storage-fencing.md). Implementation work is tracked in the template repository's `docs/superpowers/plans/2026-10-24-parity-s5-organization-quotas.md`; that repository-only plan is not a generated application's runtime dependency.
