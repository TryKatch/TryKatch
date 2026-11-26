using Trykatch.Application.Common;

namespace Trykatch.Application.Quotas;

/// <summary>Safe aggregate inspection for the current organization's human membership.</summary>
public interface IOrganizationQuotaUsageReader
{
    Task<Result<OrganizationQuotaUsage>> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record OrganizationQuotaUsage(IReadOnlyList<OrganizationQuotaUsageResource> Resources);

/// <summary>Quantities are decimal Int64 strings; null means the total is not yet known.</summary>
public sealed record OrganizationQuotaUsageResource(
    string ResourceKey, string Name, string Unit, string Mode, string? Limit, Guid? Version, string? Source,
    string? Committed, string? Reserved, string? ReconciliationHeld, string State, bool Installed);
