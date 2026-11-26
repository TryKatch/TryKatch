# Conditional object storage and upload fencing

The host provides `IConditionalObjectStorage` for quota-aware uploads alongside the legacy `IObjectStorage` port. The conditional adapter makes an object write recoverable by key and operation identity; it does not grant organization or cancellation authority. The upload admission and durable fencing workflow must authorize each call.

## Managed keys and raw storage

An example managed key shape is (the host upload workflow owns the actual layout):

```text
_module-uploads/v1/organizations/{organization-guid}/documents/{document-guid}
```

Conditional keys use the exact lowercase `_module-uploads/v1/` prefix, ASCII path segments, and a maximum of 500 characters. Empty segments, `.`-prefixed segments, `..`, backslashes, and non-ASCII characters are invalid. A write with an invalid key or identity returns `InvalidPayload`; `StatAsync` reports an invalid key as `Unowned`.

The entire `_module-uploads/` tree is reserved, including internal staging and lock files. Raw `PutAsync` and `DeleteAsync` reject this namespace before doing file I/O, including leading-slash, case, and path-resolution aliases. Raw `GetAsync` can read a managed object through the legacy port, but it returns only the payload bytes; envelope metadata is not exposed as file content. Ordinary objects outside the managed tree retain the legacy byte-for-byte format and behavior.

## Identity, revisions, and outcomes

Each provisional object records an immutable identity: the upload operation ID, original positive byte length, payload SHA-256, and normalized metadata fingerprint. Hash values are normalized without regard to hex letter case. `StatAsync` reports the actual current byte length separately, because a cancellation sentinel has a current length of zero while retaining the original payload identity and length.

The adapter publishes initial objects with create-if-absent semantics. A retry with the same identity returns `AlreadyExists`; an object with another identity or an unowned/malformed file returns `Conflict` and is left unchanged. A successful acknowledged write returns `Written`. An opaque revision identifies the exact current representation; it is not a content hash or a business authorization token.

Cancellation fencing creates a zero-byte sentinel at a missing key, or replaces a provisional object only when both the identity and its exact current revision match. A repeated request for the matching sentinel returns `AlreadyExists`, even when its supplied expected revision is stale. A stale revision, foreign identity, or unowned object returns `Conflict`. The original identity stays in the sentinel so recovery can distinguish an empty cancellation marker from an empty original upload.

Possible stat states are `Missing`, `Provisional`, `Cancellation`, `Unowned`, and `Unavailable`. Treat `Unavailable` as unknown: do not assume the write failed, retry under a different key, or release a quota hold from that result. After a timeout or lost response, use an independent `StatAsync` call to recover the visible state. `Unowned` has no trusted identity or revision.

## Local adapter behavior

The local adapter captures the canonical absolute `Storage:LocalPath` when constructed. Its namespace fingerprint binds that root, so reloading configuration cannot redirect an admitted operation to a different directory. A different endpoint, bucket, or local root has a different fingerprint; credentials are excluded from the fingerprint.

Before publishing, the adapter requires a readable stream, positive declared length, matching original length, valid content type, and a SHA-256 match. It reads no more than the declared length plus one byte and verifies the digest into a bounded disk spool. It then writes the versioned identity header and payload into a unique staging file beneath the configured root and atomically moves that closed file into place. Managed reads parse the envelope from one opened file and expose a bounded stream over only the payload. This keeps legacy object files outside the managed namespace unchanged.

Local writers coordinate through permanent lock files under `_module-uploads/v1/.locks/`, named from the canonical key. The lock file is never deleted: unlinking it could let two processes lock different inodes for the same key. Each acquisition checks that a second exclusive open fails with a recognized contention result; unsupported or uncertain locking fails closed. Lock acquisition is bounded to two seconds and each conditional storage operation to 30 seconds. Cancellation from the caller propagates. These time bounds report uncertainty; they do not make a failed write safe to forget.

The local adapter requires a coherent shared filesystem whose atomic rename and exclusive file-open behavior are honored by every cooperating writer. The adapter does not declare a list of network, overlay, or replicated filesystems supported. A process-level test exercises the selected environment, but does not qualify every deployment filesystem or storage topology.

## S3 adapter behavior and operational limits

The S3 adapter binds its configured endpoint and bucket into its namespace fingerprint. It uses conditional create and conditional replacement with the provider's opaque revision, then reads object identity and length through object metadata. S3 documents `If-None-Match` for create-if-absent and `If-Match` for matching the current ETag; an ETag is an opaque conditional token here, not the upload SHA-256. See [AWS conditional writes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).

The adapter does not require a runtime versioning probe or versioning permission. If the bucket has versioning enabled, a replacement sentinel can leave a noncurrent payload version behind. Quotas count logical uploaded bytes, not provider versions, replication, envelope metadata, or staging overhead. There is no physical-storage cleanup guarantee. Do not configure lifecycle or other external deletion for managed cancellation sentinels: sentinels are permanent and their keys must never be deleted or reused.

On Unix, the lock relies on .NET `FileStream` exclusive-share locking and verifies that it works on the actual lock file. The exact .NET 10.0.13 runtime sources are available for [Unix](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs) and [Windows](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Windows.cs). The probe and process tests are evidence for the exercised environment; they do not turn all filesystems into supported shared storage.

This adapter is one storage primitive. It does not admit uploads, prove the caller's organization authority, decide whether cancellation is safe, release a reservation, or recover a database transaction. Durable `Fencing` authority, composed upload admission/finalization, and bounded recovery remain pending S5 workflow tasks. The implementation plan is tracked in the template repository, rather than shipped as an application dependency. Do not treat this page or the adapter tests as proof that the whole quota-aware upload workflow is complete. See also [organization quota configuration](quotas.md) and [local object storage setup](local-object-storage.md).
