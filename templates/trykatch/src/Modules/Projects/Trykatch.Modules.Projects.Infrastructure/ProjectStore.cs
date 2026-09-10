using Trykatch.Application.Common;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Modules.Projects.Infrastructure;

internal sealed class ProjectStore(ApplicationDbContext dbContext) : IProjectStore
{
    public async Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        IQueryable<Project> query = dbContext.Set<Project>()
            .IgnoreQueryFilters(["LifecycleVisibility"])
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt);

        query = lifecycle switch
        {
            RecordLifecycleFilter.Active => query.Where(x => x.ArchivedAt == null && x.DeletedAt == null),
            RecordLifecycleFilter.Archived => query.Where(x => x.ArchivedAt != null && x.DeletedAt == null),
            RecordLifecycleFilter.Deleted => query.Where(x => x.DeletedAt != null),
            RecordLifecycleFilter.Recoverable => query.Where(x => x.ArchivedAt != null || x.DeletedAt != null),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => EF.Functions.ILike(x.Name, $"%{search}%"));
        }

        long count = await query.LongCountAsync(cancellationToken);
        Project[] items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        return new PagedResult<Project>(items, page, pageSize, count);
    }

    public Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken) =>
        dbContext.Set<Project>().IgnoreQueryFilters(["LifecycleVisibility"]).SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == projectId, cancellationToken);

    public async Task AddAsync(Project project, CancellationToken cancellationToken) =>
        await dbContext.Set<Project>().AddAsync(project, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
