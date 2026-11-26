# S5 conditional storage qualification

Status: upstream primitives and production conditional adapters qualified locally through S5 Task4; composed quota/upload implementation remains incomplete.

On 2026-10-24, `ObjectStorageConditionalWriteIntegrationTests` passed six cases with zero skips against the pinned `docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b` image and the repository's AWS SDK. The Release build used .NET SDK 10.0.402/runtime 10.0.13. The existing `ObjectStorageIntegrationTests` baseline also passed its one case with zero skips.

Run from the template repository root:

```sh
dotnet test templates/trykatch/tests/Trykatch.IntegrationTests -c Release \
  --filter FullyQualifiedName~ObjectStorageConditionalWriteIntegrationTests
```

The cases exercise real requests and stored bytes:

- HEAD returns exact object length, operation ID, content SHA-256 and an opaque request fingerprint after conditional creation. An independent GET returns the same fingerprint and bytes whose computed SHA-256 matches. This qualifies metadata transport; it does not implement production fingerprint canonicalization or actor binding.
- Eight concurrent create-if-absent requests produce exactly one winner. Rejected writers receive HTTP 412; a subsequent read matches the winning bytes and operation metadata.
- A zero-byte cancellation sentinel survives a storage-container restart and rejects delayed and duplicate initial writes using `If-None-Match: *`.
- Replacement using the current ETag succeeds; a fabricated stale ETag, the previous ETag after replacement, and an initial-write retry all fail without changing the sentinel.
- A test transport discards an actual successful create response. An independent read recovers the stored bytes and operation metadata; a conditional retry cannot replace them. The transport delegates the original signed request directly to its inner handler rather than sending the same request through a second `HttpClient`.
- Enabling bucket versioning retains the provisional payload as a noncurrent version after replacement with a zero-byte sentinel. A version-specific read proves the bytes remain stored.

Version retention is a counterexample to releasing a physical-byte reservation merely because the current object is empty. ADR0022 subsequently fixes `documents.storage-bytes` as logical uploaded bytes: provider versions and temporary/metadata overhead are outside the metric. The adapter needs no production versioning-probe privilege and grants no physical-cleanup guarantee. A storage ETag is a conditional-write identity, not a claimed SHA-256 digest.

The primitive qualification alone does not establish PostgreSQL reservation/finalization atomicity, actor binding, metadata/hash provenance, local-filesystem fencing, coordinator authority, reconciliation, complete process-crash recovery, or quota enforcement. It grants no authority to purge committed, archived, pending-deletion, or unidentified historical customer objects. RustFS, dependency pins, and business retention decisions are unchanged.

## Production adapter checkpoint — Task4, 2026-10-25

Both storage providers now implement the neutral `IConditionalObjectStorage` alongside the compatible raw interface. They capture their destination and expose a credential-free namespace fingerprint. Conditional keys must be canonical within `_module-uploads/v1/`; raw PUT/DELETE reject the entire `_module-uploads` namespace, including absent objects and local normalized aliases. Legacy document keys remain ordinary raw objects. Neither port supplies organization or business cancellation authority.

A bounded disk spool verifies positive declared length, actual length and SHA-256 before publication; it reads at most the declared length plus one byte. Original operation/length/hash/metadata identity survives zero-byte cancellation. Unknown responses return Unavailable; independent Stat distinguishes missing, owned provisional, cancellation, unowned and unavailable. A matching sentinel is idempotent even when the caller holds the old revision. Foreign identity, malformed metadata and stale provisional revisions never authorize replacement.

S3 uses conditional PUT and HEAD against the same pinned RustFS image above. Local storage publishes a single bounded versioned envelope through a closed same-filesystem staging file and atomic move, under a permanent per-key exclusive lock. A second independent open must prove that the runtime/filesystem enforces the lock; otherwise the adapter refuses the write. Contention is bounded at two seconds; conditional operations use a thirty-second cancellation deadline. Reads expose only the payload, with payload-relative Length/Position/Seek. Lock files must never be removed or replaced.

Focused qualification uses SDK10.0.402/runtime10.0.13, actual local files and independent OS processes, and real RustFS requests. No case is skipped:

| Selection | Qualified cases | Evidence |
| --- | ---: | --- |
| `LocalConditionalObjectStorageTests` | 15 | `conditional-local-green2.trx` qualifies14; corrected counting fixture qualifies1 in `conditional-local-bounded-green.trx`. |
| `ConditionalObjectStorageAdapterIntegrationTests` | 16 | `conditional-adapters-process-first-green.trx` qualifies12 S3 cases; four remaining/new S3 cases pass in `conditional-adapters-remaining-green.trx`. |
| `LocalConditionalStorageProcessTests` | 5 | Four cases in `conditional-adapters-remaining-green.trx`; peer exact-revision replacement/restart in `conditional-local-peer-replacement-green.trx`. |
| Existing `ObjectStorageIntegrationTests` | 1 | `conditional-adapters-remaining-green.trx`; legacy PUT/GET/DELETE and denied credentials. |
| Host `ModuleProjectGraphTests` | 12 | `conditional-host-architecture.trx`; host/module project boundaries. |

The process protocol verifies four distinct writer processes with exactly one winner and independently read bytes/identity; a held permanent lock times out without publishing, survives a killed owner and permits the next writer; globally disabled runtime locking fails closed; a separate slow writer loses to a missing-object sentinel, which survives process restart; and a peer replaces only the exact owned provisional revision, rejects stale/foreign identity, and preserves the sentinel after restart. This qualifies the exercised local filesystem on this machine, not every network filesystem or power-loss durability.

The S3 transport tests discard real successful create and cancellation responses after the server has stored the result. Independent HEAD/GET recover it; an old-revision cancellation retry returns AlreadyExists without another PUT. A nonseekable source that fails after one byte returns Unavailable and leaves no object. No mocked storage success substitutes for these cases.

Meaningful RED evidence:14 new local behaviors failed against the unavailable scaffold while the legacy round-trip passed. The initial actual adapter/process run exposed payload Length including the envelope; the bounded payload stream fixes that regression. Two S3 expectations were corrected to the actual generic port contract (intrinsic path validity, and cancellation idempotency only after a sentinel exists). The nonseekable MemoryStream-derived fixture counted the same read twice; replacing it with an independently wrapped source corrected the measurement without changing the production byte bound. An initial child-process missing IConfiguration registration was a harness error and is not claimed as adapter RED evidence.

Logs/TRX live under the ignored repository directory `.superpowers/sdd/2026-10-24-organization-quotas/`. Successful checks are reused; these counts are unique cases rather than sums of repeated runs. See [adapter operations](../../operations/conditional-object-storage.md) ([français](../../operations/conditional-object-storage.fr.md)). Durable admission, permission/actor binding, database Fencing before storage cancellation, atomic Documents/audit/outbox/usage completion, inventory and host reconciliation remain Tasks5/6. Actual composed two-replica, packed-product and whole-S5 review/CI acceptance remain Task8. A provisional storage marker must never be treated as permission to cancel committed business data.
