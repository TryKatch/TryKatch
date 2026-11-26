using System.Net.Http.Headers;

namespace Trykatch.Modules;

/// <summary>Finite bounds for an explicitly enrolled organization mutation.</summary>
public sealed record ModuleMutationPolicy(
    TimeSpan ReplayLifetime,
    int MaximumRequestBytes = 65_536,
    int MaximumResponseBytes = 262_144,
    bool KeyRequired = false)
{
    public static ModuleMutationPolicy Default { get; } = new(TimeSpan.FromHours(24));

    public void Validate()
    {
        if (ReplayLifetime < TimeSpan.FromMinutes(1) || ReplayLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(ReplayLifetime));
        if (MaximumRequestBytes is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestBytes));
        if (MaximumResponseBytes is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
    }
}

/// <summary>Request intent; organization and actor always come from the trusted host context.</summary>
public sealed record MutationRequest(
    string ModuleId,
    string OperationId,
    string KeyHash,
    string RequestHash,
    ModuleMutationPolicy Policy,
    IReadOnlyList<string> RequiredPermissions)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Policy);
        ArgumentNullException.ThrowIfNull(RequiredPermissions);
        Policy.Validate();
        if (!MutationContractRules.IsIdentifier(ModuleId, 80) || !MutationContractRules.IsIdentifier(OperationId, 160))
            throw new ArgumentException("A finite module and operation identity is required.");
        if (!MutationContractRules.IsHash(KeyHash) || !MutationContractRules.IsHash(RequestHash))
            throw new ArgumentException("Mutation identities require canonical SHA-256 hashes.");
        if (RequiredPermissions.Count is < 1 or > 32
            || RequiredPermissions.Any(permission => !MutationContractRules.IsIdentifier(permission, 160))
            || RequiredPermissions.Distinct(StringComparer.Ordinal).Count() != RequiredPermissions.Count)
            throw new ArgumentException("Explicit, finite and unique permission keys are required.");
    }
}

public sealed record MutationAuthority(
    Guid OrganizationId,
    Guid ActorId,
    Guid MembershipId,
    IReadOnlySet<string> Permissions);

/// <summary>A server-produced reference interpreted only by the owning module.</summary>
public sealed record MutationResourceReference(string ResourceKey, Guid ResourceId);

public sealed record MutationReceipt(
    int StatusCode,
    string? ContentType,
    string? Location,
    byte[] Body,
    MutationResourceReference? ResourceReference = null)
{
    public void Validate(ModuleMutationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(Body);
        policy.Validate();
        if (StatusCode is < 200 or > 299)
            throw new ArgumentException("Only successful responses can be replayed.");
        if (Body.Length > policy.MaximumResponseBytes)
            throw new ArgumentException("The response exceeds its replay limit.");
        if (ContentType is not null && (ContentType.Length > 256 || ContentType.Any(char.IsControl)
            || !MediaTypeHeaderValue.TryParse(ContentType, out _)))
            throw new ArgumentException("The replay content type is invalid.");
        if (Location is not null && (Location.Length is < 1 or > 2048 || !Location.StartsWith('/')
            || Location.StartsWith("//", StringComparison.Ordinal) || Location.Any(character => char.IsControl(character) || character == '\\')))
            throw new ArgumentException("Replay Location must be a bounded same-origin path.");
        if (ResourceReference is { } resource && (!MutationContractRules.IsIdentifier(resource.ResourceKey, 120)
            || resource.ResourceId == Guid.Empty))
            throw new ArgumentException("The replay resource reference is invalid.");
    }
}

public enum MutationReservationState { Reserved, Completed, Conflict, InProgress, Denied }

public sealed record MutationReservationResult(
    MutationReservationState State,
    MutationAuthority? Authority = null,
    Guid? ReservationId = null,
    MutationReceipt? Receipt = null);

/// <summary>Transaction-bound host persistence; implementations never commit or invoke mutations.</summary>
public interface IModuleMutationIdempotencyStore
{
    Task<MutationReservationResult> ReserveAsync(
        MutationRequest request,
        Func<MutationAuthority, CancellationToken, Task<bool>> authorizeRequest,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid reservationId, MutationReceipt receipt, CancellationToken cancellationToken = default);
}

internal static class MutationContractRules
{
    internal static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrEmpty(value) && value.Length <= maximumLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    internal static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
