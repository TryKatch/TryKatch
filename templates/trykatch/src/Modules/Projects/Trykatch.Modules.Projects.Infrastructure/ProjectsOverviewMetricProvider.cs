using Trykatch.Application.Overview;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Modules.Projects.Infrastructure;

internal sealed class ProjectsOverviewMetricProvider(ApplicationDbContext dbContext) : IWorkspaceOverviewMetricProvider
{
    public string ModuleId => "projects";

    public async Task<IReadOnlyList<WorkspaceMetric>> GetMetricsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        int count = await dbContext.Set<Project>()
            .CountAsync(project => project.OrganizationId == organizationId, cancellationToken);
        return [new WorkspaceMetric("projects.active", "Projects", count, "Active records")];
    }
}
