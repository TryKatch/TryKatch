namespace FlatpackApp.Application.Overview;

public sealed record WorkspaceOverview(
    int ActiveProjects,
    int ActiveMembers,
    int PendingInvitations,
    int ActiveRoles,
    int MembersWithAccess,
    int EventsToday,
    IReadOnlyList<WorkspaceActivity> RecentActivity);

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
