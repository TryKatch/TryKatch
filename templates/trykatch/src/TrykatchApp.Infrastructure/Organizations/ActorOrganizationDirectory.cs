using Microsoft.EntityFrameworkCore;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Domain.Organizations;
using TrykatchApp.Infrastructure.Persistence;

namespace TrykatchApp.Infrastructure.Organizations;

internal sealed class ActorOrganizationDirectory(OrganizationControlPlaneDbContext context) : IActorOrganizationDirectory
{
    public async Task<IReadOnlyList<Organization>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await (from organization in context.Organizations.AsNoTracking()
               join membership in context.Memberships.AsNoTracking() on organization.Id equals membership.OrganizationId
               where membership.UserId == userId && membership.Status == MembershipStatus.Active
                   && membership.ArchivedAt == null && membership.DeletedAt == null && organization.IsActive
               orderby organization.Name
               select organization).ToArrayAsync(cancellationToken);
}
