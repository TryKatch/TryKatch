using FlatpackApp.Application.Common;
using FlatpackApp.Domain.Projects;

namespace FlatpackApp.Application.Projects;

public interface IProjectStore
{
    Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken);
    Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken);
    Task AddAsync(Project project, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
