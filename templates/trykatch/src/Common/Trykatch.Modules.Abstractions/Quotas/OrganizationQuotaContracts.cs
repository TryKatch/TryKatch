namespace Trykatch.Modules;

/// <summary>The unit used when accounting a declared organization quota resource.</summary>
public enum ModuleQuotaUnit
{
    Bytes = 1,
    Count = 2
}

/// <summary>Static declaration of one module-owned resource that may be quota-limited.</summary>
public sealed record ModuleQuotaResourceDescriptor(
    string Key,
    string Name,
    string Description,
    ModuleQuotaUnit Unit);

/// <summary>One explicit generated-manifest expectation for a module quota resource.</summary>
public sealed record InstalledModuleQuotaResource(string ModuleId, ModuleQuotaResourceDescriptor Resource);

/// <summary>Disabled and explicit Unlimited are distinct from an enforced missing limit.</summary>
public enum OrganizationEntitlementMode
{
    Disabled,
    Finite,
    Unlimited,
    Unconfigured
}

/// <summary>Safe host projection data. This record does not itself grant access or installation.</summary>
public sealed record OrganizationEntitlement(
    string ResourceKey,
    OrganizationEntitlementMode Mode,
    long? Limit,
    Guid Version,
    string Source,
    DateTimeOffset EffectiveAtUtc);

public enum ModuleQuotaReservationState
{
    Reserved,
    Committed,
    Released
}

/// <summary>Durable hold identity; the host supplies its organization and actor scope.</summary>
public sealed record ModuleQuotaReservation(
    Guid Id,
    string ResourceKey,
    long Units,
    Guid OperationId,
    Guid EntitlementVersion,
    DateTimeOffset ExpiresAtUtc,
    ModuleQuotaReservationState State);

public enum QuotaReservationDisposition
{
    Reserved,
    AlreadyReserved,
    Denied,
    Unavailable,
    Conflict
}

/// <summary>Only Reserved and AlreadyReserved carry a reservation; failures use safe codes.</summary>
public sealed record QuotaReservationResult(
    QuotaReservationDisposition Disposition,
    ModuleQuotaReservation? Reservation = null,
    string? FailureCode = null);

public interface IOrganizationEntitlementReader
{
    Task<OrganizationEntitlement> GetAsync(string resourceKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Transactional quota holds in the current trusted organization scope. External uploads use
/// durable upload admission so expiry cannot release capacity while an object write is live.
/// </summary>
public interface IOrganizationQuota
{
    Task<QuotaReservationResult> ReserveAsync(
        string resourceKey, long units, Guid operationId, CancellationToken cancellationToken = default);
    Task CommitAsync(Guid reservationId, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default);
}
