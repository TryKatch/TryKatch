# Organization mutation idempotency

[Français](idempotency.fr.md)

Idempotency is an optional, explicitly enrolled safeguard for organization-scoped HTTP mutations. It lets a client retry one logical operation after an uncertain network outcome without running the module use case twice within the replay window. It does not make every write endpoint idempotent automatically, and it does not add a client UI, feature flag, or public administration API.

## Upgrade before deployment

Apply application migration `20261024113623_AddMutationReceipts` with the existing one-shot privileged migrator before deploying an API that uses mutation idempotency. The migration creates `app.mutation_receipts` with FORCE RLS and organization-and-actor scoped access. Keep the existing five least-privilege runtime roles and their ownership boundaries; this feature adds no runtime role or runtime owner. Never provide the migrator connection to the API. Follow the existing [database migration workflow](../database.md).

The receipt table contains private response bytes and must be protected like other organization data. The raw idempotency key and original request JSON are not stored separately: the key and canonical request identity are stored as SHA-256 hashes. Stored responses can still contain submitted fields. The table has no automatic purge job or separate retention policy. Expiry makes a receipt eligible for replacement when that identity is used again; it does not by itself remove the row.

## Enrollment and compatibility

An endpoint must explicitly opt in through `WithMutationIdempotency` and provide module-owned request and replay authorization gates. The host validates that an enrolled endpoint is a named, secured organization mutation with bounded, non-streaming response metadata. An endpoint that is not enrolled ignores this feature. For an enrolled endpoint, the `Idempotency-Key` header is optional by default: without it, the existing request proceeds through the ordinary endpoint path and no receipt is reserved. Setting `ModuleMutationPolicy.KeyRequired` makes the header mandatory for that endpoint.

The current Projects module enrolls these five operations:

| Operation ID | Route | Resource bound on success |
| --- | --- | --- |
| `Projects_Create` | `POST /api/v1/projects/` | Server-created project ID |
| `Projects_Update` | `PUT /api/v1/projects/{id}` | Route project ID |
| `Projects_Archive` | `POST /api/v1/projects/{id}/archive` | Route project ID |
| `Projects_Restore` | `POST /api/v1/projects/{id}/restore` | Route project ID |
| `Projects_Delete` | `DELETE /api/v1/projects/{id}` | Route project ID |

Projects records the resource with `ModuleMutationIdempotency.BindResource` only after its use case succeeds. See [the Projects endpoint enrollment](../../src/Modules/Projects/Trykatch.Modules.Projects.Presentation/ProjectsEndpoints.cs) and [host idempotency pipeline](../../src/Common/Trykatch.Modules.AspNetCore/ModuleIdempotency.cs). Organization creation remains on its existing recovery flow.

Do not enroll file or stream responses, server-sent events, downloads, or operations that issue expiring or signed URLs. This is an endpoint-author enrollment rule; the host does not inspect arbitrary JSON to discover URL semantics. Only finite responses that are safe to return again as stored bytes are suitable.

## Keys, request identity, and limits

Provide exactly one ASCII key from 1 through 128 characters. The first character must be an ASCII letter or digit; later characters may also be `.`, `_`, `:`, or `-`. Duplicate header values, empty values, non-ASCII characters, and other punctuation are rejected. Choose one key for a logical user intent, retain it for retries, and use a new key for a genuinely new intent. Do not generate a replacement key just because a response was lost: that could authorize a second write.

The default policy makes a successful receipt replayable for 24 hours. An endpoint may select a lifetime from 1 minute through 24 hours. The effective window ends at the earlier of the stored expiry and original completion plus the current policy lifetime. After expiry, the same key can execute a new mutation; if the original outcome is still uncertain, reconcile it through authorized reads before submitting again. The default request limit is 65,536 UTF-8 bytes (64 KiB), and the policy cannot raise it above that value. Non-empty request bodies must be strict UTF-8 JSON with `application/json` or an `application/*+json` media type. Compressed request bodies are not accepted.

Preparation returns **400** for missing required/invalid keys or malformed JSON, **413** for an oversized body, and **415** for unsupported media or content encoding. These rejections do not invoke the mutation.

The request fingerprint includes the uppercase HTTP method, escaped path (including the path base), raw query string, normalized JSON media type, and canonical body identity. JSON object property order and whitespace do not change the canonical body; array order remains significant, numeric values are normalized, and duplicate object property names are rejected. A missing body is distinct from a JSON body. The canonical JSON algorithm is shared through [`ModuleCanonicalJson`](../../src/Common/Trykatch.Modules.Abstractions/Serialization/ModuleCanonicalJson.cs).

The response receipt limit defaults to 262,144 bytes (256 KiB). A policy may raise it to at most 1,048,576 bytes (1 MiB), and the host's outer transactional response limit may clamp it further. Only 2xx results can be saved. For a newly reserved execution, a non-2xx response rolls back the mutation transaction and reservation, so a corrected request can retry with the same key. Oversized, unserializable, canceled, or failed-to-commit responses do not produce a completed receipt.

## Authorization and outcomes

Normal authentication, workspace and organization resolution, endpoint authorization, antiforgery validation, and the organization transaction remain in force. For a keyed request, the store resolves current organization, actor, membership, and permissions before returning a reservation, receipt, conflict, or busy result. The advisory lock attempt happens first, but its outcome is not disclosed before these checks. The module request gate checks the target; a completed receipt also passes the module's replay gate against the current authority and resource. A replay returns the receipt without invoking the endpoint use case again. Projects requires current `projects.manage` authority and verifies the project under the current organization, including on replay.

Authentication, session, workspace, authorization, or antiforgery middleware can reject a request before the idempotency filter returns an idempotency result. Their status codes remain their own. Once the filter handles a key, these outcomes apply:

| Outcome | Response | Client action |
| --- | --- | --- |
| Same organization, actor, module, operation, key, and request identity has a completed receipt | Replays the stored 2xx response | Treat it as the result of the original logical intent |
| Same identity and key, but a different request fingerprint | `409 idempotency_conflict` | Do not change the key to hide a mismatch; inspect the pending intent and payload |
| Another transaction currently holds the identity | `409 idempotency_in_progress`, with `Retry-After: 1` | Retry the same request with the same key after the indicated delay |
| The current request or replay authorization gate denies access | Generic forbidden result from the idempotency filter | Re-establish valid access; do not infer whether a receipt exists |

Identity is scoped by organization, actor, module, operation, and hashed key. A different actor or operation does not share another caller's receipt. A replay must still pass current authorization and module resource checks. The filter does not re-run a business use case for a completed receipt.

The receipt preserves only the HTTP status, content type, optional root-relative `Location`, and exact response body bytes. It does not restore other response headers or cookies, including cookies from an earlier response. Do not use a receipt as a source of fresh cookies or expiring URLs.

## Client example

Generate the key once when the user starts the logical operation, keep it with the pending operation, and resend the same payload and key after a timeout or lost response. The example obtains the existing same-origin antiforgery token and sends the normal HttpOnly-cookie credentials:

```ts
const intentKey = crypto.randomUUID(); // Create once per user intent; retain until its outcome is known.
const payload = { name: "Launch", description: "Roadmap" };

async function submitCreate() {
  const antiforgery = await fetch("/api/v1/auth/antiforgery", {
    credentials: "include",
  });
  if (!antiforgery.ok) throw new Error("Antiforgery request failed");
  const { token } = await antiforgery.json() as { token: string };

  return fetch("/api/v1/projects/", {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      "X-CSRF-TOKEN": token,
      "Idempotency-Key": intentKey,
    },
    body: JSON.stringify(payload),
  });
}

// If the network outcome is uncertain, call submitCreate() again with this same key and payload.
```

The key is not a secret or an authorization credential. Do not log keys, request bodies, or receipt bodies. There is no per-key logging, metrics, or public admin console for inspecting receipts. Apply the organization's normal private-data access and database backup controls to the receipt table.
