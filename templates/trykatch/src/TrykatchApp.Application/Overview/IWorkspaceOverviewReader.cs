namespace TrykatchApp.Application.Overview;

public sealed record WorkspaceOverview(
    IReadOnlyList<WorkspaceMetric> ModuleMetrics,
    int ActiveMembers,
    int PendingInvitations,
    int ActiveRoles,
    int MembersWithAccess,
    int EventsToday,
    IReadOnlyList<WorkspaceActivity> RecentActivity);

public sealed record WorkspaceMetric(
    string Id,
    string Label,
    long Value,
    string Note);

public sealed record WorkspaceActivity(
    string Action,
    string Title,
    string TargetDisplayName,
    string ActorDisplayName,
    DateTimeOffset OccurredAt);

public interface IWorkspaceOverviewReader
{
    Task<WorkspaceOverview> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Allows enabled modules to contribute overview data without the workspace
/// shell querying module-owned entities or depending on module implementations.
/// </summary>
public interface IWorkspaceOverviewMetricProvider
{
    string ModuleId { get; }
    Task<IReadOnlyList<WorkspaceMetric>> GetMetricsAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
