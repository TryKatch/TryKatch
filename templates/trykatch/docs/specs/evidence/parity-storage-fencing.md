# S5 conditional storage qualification

Status: upstream storage primitives qualified locally; quota implementation remains incomplete.

On 2026-10-24, `ObjectStorageConditionalWriteIntegrationTests` passed five cases with zero skips against the pinned `docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b` image and the repository's AWS SDK. The Release build used .NET SDK 10.0.402/runtime 10.0.13. The existing `ObjectStorageIntegrationTests` baseline also passed its one case with zero skips.

Run from the template repository root:

```sh
dotnet test templates/trykatch/tests/Trykatch.IntegrationTests -c Release \
  --filter FullyQualifiedName~ObjectStorageConditionalWriteIntegrationTests
```

The cases exercise real requests and stored bytes:

- Eight concurrent create-if-absent requests produce exactly one winner. Rejected writers receive HTTP 412; a subsequent read matches the winning bytes and operation metadata.
- A zero-byte cancellation sentinel survives a storage-container restart and rejects delayed and duplicate initial writes using `If-None-Match: *`.
- Replacement using the current ETag succeeds; a fabricated stale ETag, the previous ETag after replacement, and an initial-write retry all fail without changing the sentinel.
- A test transport discards an actual successful create response. An independent read recovers the stored bytes and operation metadata; a conditional retry cannot replace them. The transport delegates the original signed request directly to its inner handler rather than sending the same request through a second `HttpClient`.
- Enabling bucket versioning retains the provisional payload as a noncurrent version after replacement with a zero-byte sentinel. A version-specific read proves the bytes remain stored.

Version retention is a counterexample to releasing a physical-byte reservation merely because the current object is empty. Production adoption must establish compatible versioning and lifecycle constraints, or account for retained versions; these tests do not choose that policy. A storage ETag is a conditional-write identity, not a claimed SHA-256 digest.

This qualification does not establish PostgreSQL reservation/finalization atomicity, actor binding, metadata/hash provenance, local-filesystem fencing, coordinator authority, reconciliation, complete process-crash recovery, or quota enforcement. Those remain S5 implementation and acceptance gates. It grants no authority to purge committed, archived, pending-deletion, or unidentified historical customer objects. RustFS, dependency pins, and business retention decisions are unchanged.
