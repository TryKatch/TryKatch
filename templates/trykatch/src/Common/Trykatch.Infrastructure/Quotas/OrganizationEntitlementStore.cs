using Microsoft.EntityFrameworkCore;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class OrganizationEntitlementStore(ApplicationDbContext application, ModuleCatalog catalog)
{
    internal bool TryGetResource(string resourceKey, out string owner, out ModuleQuotaResourceDescriptor? resource)
    {
        foreach (ModuleDescriptor module in catalog.Descriptors)
        {
            resource = module.QuotaResources.SingleOrDefault(candidate => candidate.Key == resourceKey);
            if (resource is null) continue;
            owner = module.Id;
            return true;
        }
        owner = string.Empty;
        resource = null;
        return false;
    }

    internal Task<OrganizationEntitlementRecord?> ReadAsync(Guid organizationId, string resourceKey,
        CancellationToken cancellationToken) => application.Set<OrganizationEntitlementRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == organizationId && row.ResourceKey == resourceKey, cancellationToken);

    internal static OrganizationEntitlement ToContract(OrganizationEntitlementRecord record) => new(
        record.ResourceKey, record.Mode, record.Limit, record.Version, record.Source, record.EffectiveAtUtc);
}
