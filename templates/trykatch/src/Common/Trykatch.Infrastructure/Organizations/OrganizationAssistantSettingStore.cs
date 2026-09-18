using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationAssistantSettingStore(OrganizationControlPlaneDbContext db, IOrganizationContext context) : IOrganizationAssistantSettingStore
{
    public Task<OrganizationAssistantSetting?> FindAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.IsResolved && organizationId == context.OrganizationId
            ? db.Set<OrganizationAssistantSetting>().SingleOrDefaultAsync(setting => setting.OrganizationId == context.OrganizationId, cancellationToken)
            : Task.FromResult<OrganizationAssistantSetting?>(null);

    public async Task AddAsync(OrganizationAssistantSetting setting, CancellationToken cancellationToken)
    {
        if (!context.IsResolved || setting.OrganizationId != context.OrganizationId)
            throw new UnauthorizedAccessException("An active matching organization is required.");
        await db.Set<OrganizationAssistantSetting>().AddAsync(setting, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
