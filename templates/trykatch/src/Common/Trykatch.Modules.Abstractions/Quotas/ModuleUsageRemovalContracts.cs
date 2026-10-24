namespace Trykatch.Modules;

/// <summary>A stable identity for one registered module usage-removal policy.</summary>
public sealed record ModuleUsageRemovalPolicyRef(
    string OwnerModuleId,
    string PolicyKey,
    int PolicyVersion);

/// <summary>Bounded source evidence presented for module confirmation of a logical usage removal.</summary>
public sealed record ModuleUsageRemovalContext(
    Guid SourceId,
    Guid SourceEffectId,
    long OriginalUnits,
    long RequestedUnits,
    Guid OperationId);

/// <summary>Module-owned confirmation hook; it does not directly mutate the host usage ledger.</summary>
public interface IModuleUsageRemovalPolicy
{
    Task<bool> ConfirmAsync(
        ModuleUsageRemovalContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Associates a declared removal policy with its implementation at the host composition boundary.</summary>
public sealed record ModuleUsageRemovalPolicyBinding(
    ModuleUsageRemovalPolicyRef Policy,
    IModuleUsageRemovalPolicy Adapter);

public enum OrganizationUsageRemovalDisposition
{
    Recorded,
    AlreadyRecorded,
    Denied,
    Conflict,
    Unavailable
}

public sealed record OrganizationUsageRemovalResult(
    OrganizationUsageRemovalDisposition Disposition,
    string? FailureCode = null);

/// <summary>Host-owned append-only logical usage effects; callers supply no organization or actor authority.</summary>
public interface IOrganizationUsageLedger
{
    Task<OrganizationUsageRemovalResult> RecordRemovalAsync(
        string resourceKey,
        Guid sourceEffectId,
        long units,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
