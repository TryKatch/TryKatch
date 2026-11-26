namespace Trykatch.Modules;

/// <summary>Original payload identity retained even when its current object becomes a zero-byte sentinel.</summary>
public sealed record ObjectWriteIdentity(Guid OperationId, long OriginalLength, string Sha256, string MetadataFingerprint);

/// <summary>Opaque provider revision; never interpret it as a content hash or business authority.</summary>
public sealed record ObjectStorageRevision(string Value);

public enum ConditionalObjectState { Missing, Provisional, Cancellation, Unowned, Unavailable }

/// <summary>Length is the current payload length, separate from Identity.OriginalLength.</summary>
public sealed record ConditionalObjectStat(ConditionalObjectState State, long? Length = null,
    ObjectWriteIdentity? Identity = null, ObjectStorageRevision? Revision = null);

public enum ConditionalObjectWriteDisposition { Written, AlreadyExists, Conflict, InvalidPayload, Unavailable }

/// <summary>Only an acknowledged write is Written. Unknown effects require independent Stat recovery.</summary>
public sealed record ConditionalObjectWriteResult(ConditionalObjectWriteDisposition Disposition, ConditionalObjectStat? Snapshot = null);

/// <summary>
/// Conditional operations in the host-managed _module-uploads/v1/ namespace. Raw Put/Delete cannot target it.
/// This port supplies no organization or cancellation authority: the host must first admit an upload or
/// durably enter Fencing. A Provisional marker alone does not authorize cancelling committed business data.
/// Local storage requires a shared, coherent filesystem with enforced cross-process locks and atomic rename.
/// </summary>
public interface IConditionalObjectStorage
{
    /// <summary>Stable identity of the captured actual provider destination, excluding credentials.</summary>
    string NamespaceFingerprint { get; }

    /// <summary>Verify exact declared length/SHA before atomically creating a never-overwritten payload.</summary>
    Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length, string contentType,
        ObjectWriteIdentity identity, CancellationToken cancellationToken = default);

    Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// A null revision creates a sentinel only when missing. Otherwise replace the exact owned revision.
    /// An existing matching sentinel is idempotent even with a stale expected revision. Foreign/unowned
    /// objects never change. The original identity remains intact and the current length becomes zero.
    /// </summary>
    Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key, ObjectStorageRevision? expectedVersion,
        ObjectWriteIdentity identity, CancellationToken cancellationToken = default);
}
