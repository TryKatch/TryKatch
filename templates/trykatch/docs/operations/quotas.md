# Organization quota configuration

Host configuration is the initial entitlement source; Billing is optional and is not involved in this path. There is no HTTP allowance-editing endpoint. Run the forward migrator before starting upgraded APIs or workers. It creates the application projection and append-only platform publication history with the existing least-privilege profiles and forced organization-scoped RLS.

The default is disabled enforcement at revision zero:

```json
{ "Quotas": { "EnforcementEnabled": false, "Revision": 0 } }
```

To enforce limits, choose a positive monotonically increasing revision and explicit rules for installed resources. This example illustrates syntax; its byte limit is not a commercial default:

```json
{
  "Quotas": {
    "EnforcementEnabled": true,
    "Revision": 1,
    "Resources": {
      "documents.storage-bytes": { "Limit": 104857600 }
    }
  }
}
```

A finite limit is a nonnegative Int64, including zero. `{ "Unlimited": true }` explicitly removes the ceiling for that resource. An omitted resource under enforcement is unconfigured and denies new allocation. Disabled enforcement records admission/usage identity without imposing a ceiling. It does not override a newer durable enforced revision. Turning enforcement off after enabling it therefore requires a newer positive revision with `EnforcementEnabled: false`.

Use the documented property spelling and exact, case-sensitive catalog resource keys. Unknown keys/properties, malformed values, negative/overflowing numbers, combined finite/Unlimited rules and enforcement at revision zero fail startup. Revision zero permits only the disabled configuration without explicit resource rules. Configuration is captured once per process; restart with a new revision to change it. Changing only the revision preserves the content hash. Installing a module does not itself change that hash. Remove an obsolete explicit resource rule in a newer configuration before removing its module; startup rejects rules for absent modules.

Before a scope takes its first quota resource lock, the host publishes a complete sorted batch using its own platform-profile transaction. It validates the same direct PostgreSQL host/port/database and server identity as the enlisted organization connection. Routed/dedicated targets are unsupported by this publisher. The source actor is a fixed technical audit identity; it receives no fabricated organization membership. Publication commits independently of later business rollback.

A new revision updates current and retained resources atomically, preserving retained owner/unit identity. The history header persists even with no installed resources. Equal revision/equal hash may initialize newly installed resources but never rewrites existing rows. Equal revision/different content fails closed; an older process uses the newer durable projection and cannot initialize missing resources with its older policy. Missing or inconsistent provenance is unavailable, never an implicit disabled or Unlimited allowance. Every new allocation rereads the projection under the shared resource lock; admitted retries and valid completion/release survive a downgrade.

Busy publication rolls back the entire batch for retry. Database failure is unavailable; no cache or process-local allowance supplies a fallback. The publication path uses bounded timeouts. Do not delete history, reset revisions or manually edit projection rows to resolve a configuration conflict; deploy the intended policy at a newer revision. Raw database credentials are not a supported editing interface.

The upload recovery migration adds immutable upload-policy key/version provenance and a closed maintenance queue. New upload intents require an explicit policy identity. Historical intents without that identity retain their reservations; the migration neither assigns a policy nor releases capacity. Do not manually bind those rows to a current policy. Recovery requires verified durable provenance.

The queue stores organization/resource/reservation/upload references and bounded scheduling state only. Organization runtime can append initial queued intents; the existing coordinator can read/update work but cannot delete it or read business quota rows. Completed references cannot reopen. Installing this schema does not execute recovery work.

Startup validates exact versioned workflow declarations and scoped bindings without reading inventory. Documents 1.3.0 registers a lifecycle-inclusive inventory source: active, archived and pending-deletion uploaded rows count; metadata-only rows do not. Unresolved sizes fail closed. The host reconciles at most 500 entries per independent transaction under the same resource lock as live writers, checks source/units when deduplicating effects, and commits usage with its source-versioned checkpoint. A stale or incomplete inventory cannot satisfy finite admission.

Documents uploads now use durable child admission before conditional object I/O. Metadata, audit, outbox, positive usage and completion share the existing outer transaction. Confirmed rollback requests host fencing rather than a raw object deletion. Missing/uncertain storage identity retains the hold. Confirmed logical removal requires the resource owner's registered read-only policy and original positive charge; Documents has no purge/refund policy. Archive, restore and deletion-request transitions retain usage.

## Bounded reconciliation

The host quota worker runs independently of quota enforcement and module job schedules. Keep the existing work-coordinator connection configured, including when disabling/removing a module with outstanding uploads. No new runtime profile is introduced. Organization runtime inserts closed intents; the coordinator reads/updates only their scheduling state. It cannot read business quota data or delete work. Public transaction-only reservations enqueue their expiry reference in the same parent transaction; durable uploads and incomplete inventory already enqueue their own references.

A worker claims at most 20 items per batch using database time, row exclusion and a two-minute lease. Each execution has a 60 second deadline, bounded database operations and fresh fixed-principal organization transactions. The principal has no membership or module permissions. Transaction-only expiry verifies its immutable effect kind and durable reference under the resource lock. Upload recovery verifies the matching intent/hold, commits Fencing, ends that transaction before storage I/O, independently confirms a permanent cancellation sentinel, and only then releases the hold in another transaction. It does not purge committed objects or physical provider versions. All replicas must retain the admitted storage namespace; namespace mismatch stays unavailable with the hold retained.

Completion is acknowledged only under the current unexpired lease. A lost worker is recoverable after lease expiry; stale acknowledgements cannot change a new lease. Retry uses database-time backoff from 5 seconds to 5 minutes, without a terminal attempt cutoff. In-progress pages retry after 5 seconds. Inactive/missing organizations, unsupported placement or an absent inventory source pause and are reconsidered after 60 seconds. Re-enabling a source resumes its retained checkpoint. The host can fence a previously admitted upload with its module absent and original membership revoked; it does not load that module's business handler. Unbound historical upload-policy identity remains unavailable rather than inventing authority.

The readiness check `quota-maintenance` reports only pending/retrying/paused counts and oldest due age. It is unhealthy until a successful coordination observation, or when coordination is unavailable; retrying/paused work or overdue work beyond 5 minutes degrades it. Future due work does not age from its creation timestamp. Failure logs contain exception types and closed work kinds, without payloads, keys or actor provenance.

Local component evidence is recorded in [quota maintenance evidence](../specs/evidence/parity-quota-maintenance.md). Actual process-loss/restart and composed RustFS replica journeys, request throttling, safe usage UI, packed generated products and final S5 review/CI remain required. These component checks do not establish whole-product capacity guarantees.
