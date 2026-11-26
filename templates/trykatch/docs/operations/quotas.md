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

A new revision updates current and retained resources atomically, preserving retained owner/unit identity. The history header persists even with no installed resources. Equal revision/equal hash may initialize newly installed resources but never rewrites existing allowance values or versions. Installed display names can be refreshed independently without changing policy history. Equal revision/different content fails closed; an older process uses the newer durable projection and cannot initialize missing resources with its older policy. Missing or inconsistent provenance is unavailable, never an implicit disabled or Unlimited allowance. Every new allocation rereads the projection under the shared resource lock; admitted retries and valid completion/release survive a downgrade.

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

Local evidence is recorded in [quota maintenance evidence](../specs/evidence/parity-quota-maintenance.md). Actual OS-process kills/restarts and uncertain storage responses are qualified against PostgreSQL/local storage. Composed RustFS replica journeys, an actual generated-application usage journey, packed generated products and final S5 review/CI remain required. These local checks do not establish whole-product capacity guarantees.

## Current organization usage

Settings includes a read-only Usage tab for memberships with `organizations.read` or `organizations.manage`. Switching tabs preserves unsaved AI configuration, model and key fields. `GET /api/v1/quotas/usage` (`Quotas_CurrentUsage`) uses only the resolved workspace; query parameters and organization headers cannot select another organization's data. The endpoint exposes aggregate resource information, without object keys, actor/effect identities or editable allowances. Permission rejection precedes request counting, and the reader rechecks the actual transaction, active membership and permission.

Limit, committed, reserved and reconciliation-held quantities are decimal Int64 strings. Clients must preserve those strings and use exact arithmetic, such as JavaScript BigInt. The database reads projection, inventory and aggregates in one statement snapshot. Expired reservations and uploads already being fenced remain in reconciliation-held usage until safe release; expiry does not refund them. Arithmetic overflow is unavailable, never truncated.

`Ready` requires a complete inventory from the exact installed source version. `Initializing` leaves committed usage unknown and requests bounded inventory through the existing closed queue, including when limits are disabled. That intent belongs to the caller's transaction; inspection never commits it independently or scans a module's business tables. Known holds remain visible. `Paused` retains history when the owner/source is absent; `Unavailable` reports an invalid or unavailable projection. A coordination outage returns sanitized 503 `quota_usage_unavailable`, with no invented capacity. Disabled limits still track usage.

Apply the forward display-name migration before the upgraded API. Names are display metadata, not authority. The publisher retains them when a module is removed and may refresh legacy names without changing allowance versions/history. An already-absent legacy resource whose name was never recorded uses its public catalog identity as the label. See [usage inspection evidence](../specs/evidence/parity-quota-usage.md) for the qualified component checks and remaining composed-product gates.


## Organization request throttling

Request throttling is separate from storage capacity and is disabled by default. To enable the shared `organization-api` allowance, configure the following on every API replica and restart with matching values:

```json
{
  "OrganizationRequestLimits": {
    "Enabled": true,
    "PermitLimit": 300,
    "WindowSeconds": 60
  }
}
```

The opt-in defaults are 300 attempts per 60 seconds. Permit limits must be 1–1,000,000 and windows 1–3,600 seconds. Unknown properties and invalid values fail startup, including when disabled. Deploy matching enabled/limit/window settings across replicas; a disabled replica cannot provide the distributed guarantee. Process restart retains the current database window and count. Admission uses the same captured organization database as authorization; changing its connection configuration requires the normal host restart.

Requests that pass endpoint authorization and cookie verification consume a permit before feature checks, idempotency and business handling. Later validation failure, feature rejection, replay or business rollback does not refund it. Endpoint permission and antiforgery rejections consume no permit. The organization comes from the resolved workspace, never an organization header. Existing actor/IP/authentication/assistant controls still run earlier.

PostgreSQL starts and advances the shared window; one current row per organization/policy is retained in `app.organization_request_limits`. The organization runtime has only scoped SELECT/INSERT/UPDATE; other profiles receive no access, and runtime DELETE is denied. Apply the forward application migration and normal runtime provisioning before enabling the policy. No new connection profile is needed.

An exhausted allowance returns 429 with database-derived `Retry-After` seconds. Unavailable coordination returns 503 (`organization_request_limit_unavailable`); retry after service recovery. Admission uses an independent short transaction with a four-second deadline, 500 ms lock timeout and two-second statement timeout. No process-local or cache allowance substitutes for PostgreSQL. An optional Redis read-cache outage does not reset the allowance. See [request throttling evidence](../specs/evidence/parity-organization-throttling.md) for qualified cases and remaining composed-product gates.
