namespace Trykatch.Modules;

/// <summary>A module-owned, versioned upload-policy identity without permission authority.</summary>
public sealed record ModuleUploadPolicyRef(string OwnerModuleId, string PolicyKey, int PolicyVersion);

/// <summary>An explicit upload workflow declaration attached to one byte quota resource.</summary>
public sealed record ModuleUploadPolicyDescriptor(
    string OwnerModuleId,
    string PolicyKey,
    int PolicyVersion,
    string RequiredPermission);

/// <summary>Upload metadata presented to host admission; these values supply no caller authority.</summary>
public sealed record ModuleUploadRequest(
    ModuleUploadPolicyRef Policy,
    Guid OperationId,
    long Length,
    string Sha256,
    string MetadataFingerprint);

/// <summary>
/// Opaque host-issued capability for one admitted upload. Implementations must be host-private;
/// public fields and a module-created subclass do not establish issuer, caller, or transaction authority.
/// </summary>
public abstract class ModuleUploadReceipt
{
    protected ModuleUploadReceipt() { }

    public abstract ModuleUploadPolicyRef Policy { get; }
    public abstract Guid OperationId { get; }
    public abstract Guid SubjectId { get; }
    public abstract string ObjectKey { get; }
    public abstract ObjectWriteIdentity Identity { get; }
    public abstract DateTimeOffset ExpiresAtUtc { get; }

    public sealed override string ToString() => "[opaque upload receipt]";
}

public enum ModuleUploadReserveDisposition
{
    Reserved,
    AlreadyReserved,
    AlreadyCommitted,
    Denied,
    Conflict,
    Unavailable
}

public sealed record ModuleUploadReserveResult(
    ModuleUploadReserveDisposition Disposition,
    ModuleUploadReceipt? Receipt = null,
    string? FailureCode = null);

public enum ModuleUploadFinalizationDisposition
{
    Committed,
    AlreadyCommitted,
    Conflict,
    Unavailable
}

public sealed record ModuleUploadFinalization<T>(
    ModuleUploadFinalizationDisposition Disposition,
    T? Value = default,
    Guid? SubjectId = null,
    string? FailureCode = null);

public enum ModuleUploadCancelDisposition
{
    Cancelled,
    AlreadyCancelled,
    Conflict,
    Unavailable
}

public sealed record ModuleUploadCancelResult(
    ModuleUploadCancelDisposition Disposition,
    string? FailureCode = null);

/// <summary>Durable host-controlled admission and finalization for module-owned object uploads.</summary>
public interface IModuleUploadAdmission
{
    Task<ModuleUploadReserveResult> ReserveAsync(
        ModuleUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<ModuleUploadFinalization<T>> FinalizeAsync<T>(
        ModuleUploadReceipt receipt,
        Func<CancellationToken, Task<T>> finalize,
        CancellationToken cancellationToken = default);

    Task<ModuleUploadCancelResult> CancelAsync(
        ModuleUploadReceipt receipt,
        CancellationToken cancellationToken = default);
}
