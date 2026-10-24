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

At the Task3 checkpoint, the host publication seam and transaction-only quota API are implemented. Documents upload admission/inventory, abandoned-upload reconciliation, request throttling and the usage UI are subsequent S5 work. Publication tests do not establish storage upload or whole-product capacity guarantees.
