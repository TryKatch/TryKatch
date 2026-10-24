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

At this Task5 persistence checkpoint, publication, the transaction-only quota API and conditional storage adapters are implemented. Startup now validates exact versioned workflow declarations and scoped adapter bindings without reading inventory. Documents1.3.0 registers its own inventory source: bounded pages include uploaded active, archived and pending-deletion rows, preserve organization isolation, exclude metadata-only records and refuse unresolved sizes. Legacy rows retain NULL upload identity and use their stable document ID for inventory. The host reconciler processes at most500 entries per independent transaction under the same resource lock used by quota writers. It checks existing effect source/units before deduplicating, bulk-appends positive effects, verifies Int64 totals and commits the source-versioned cursor/completion checkpoint atomically. A stale source version cannot satisfy finite admission. Missing/inactive organizations and unsupported placement pause; malformed inventory, conflicting effects, overflow or unavailable coordination never advance the page. The fixed host principal has no membership or business permissions. The maintenance queue executor and composite upload/removal services remain unfinished. Existing Documents uploads do not yet use durable quota admission. Abandoned-upload reconciliation, request throttling and the usage UI also remain required; these component checks do not establish whole-product capacity guarantees.
