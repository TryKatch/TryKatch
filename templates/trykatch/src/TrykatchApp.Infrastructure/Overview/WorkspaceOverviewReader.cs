using TrykatchApp.Application.Auditing;
using TrykatchApp.Application.Identity;
using TrykatchApp.Application.Overview;
using TrykatchApp.Domain.Organizations;
using TrykatchApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TrykatchApp.Infrastructure.Overview;

internal sealed class WorkspaceOverviewReader(
    ApplicationDbContext applicationDbContext,
    PlatformDbContext platformDbContext,
    IUserDirectory users,
    IEnumerable<IWorkspaceOverviewMetricProvider> metricProviders) : IWorkspaceOverviewReader
{
    public async Task<WorkspaceOverview> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset today = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        WorkspaceMetric[] moduleMetrics = (await Task.WhenAll(metricProviders
                .OrderBy(provider => provider.ModuleId, StringComparer.Ordinal)
                .Select(provider => provider.GetMetricsAsync(organizationId, cancellationToken))))
            .SelectMany(metrics => metrics)
            .ToArray();
        int activeMembers = await platformDbContext.Memberships
            .CountAsync(membership => membership.OrganizationId == organizationId
                && membership.ArchivedAt == null
                && membership.DeletedAt == null
                && membership.Status == MembershipStatus.Active, cancellationToken);
        int membersWithAccess = await platformDbContext.Memberships
            .CountAsync(membership => membership.OrganizationId == organizationId
                && membership.ArchivedAt == null
                && membership.DeletedAt == null
                && membership.Status == MembershipStatus.Active
                && membership.Roles.Any(), cancellationToken);
        int pendingInvitations = await platformDbContext.Invitations
            .CountAsync(invitation => invitation.OrganizationId == organizationId
                && invitation.ArchivedAt == null
                && invitation.DeletedAt == null
                && invitation.AcceptedAt == null
                && invitation.RevokedAt == null
                && invitation.ExpiresAt > now, cancellationToken);
        int activeRoles = await platformDbContext.Roles
            .CountAsync(role => role.OrganizationId == organizationId
                && role.ArchivedAt == null
                && role.DeletedAt == null, cancellationToken);
        int eventsToday = await applicationDbContext.AuditEntries
            .CountAsync(entry => entry.OrganizationId == organizationId && entry.OccurredAt >= today, cancellationToken);

        AuditEntry[] recentEntries = await applicationDbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(5)
            .ToArrayAsync(cancellationToken);
        IReadOnlyDictionary<Guid, UserSummary> actors = await users.GetUsersAsync(
            recentEntries.Select(entry => entry.ActorId), cancellationToken);
        WorkspaceActivity[] recentActivity = recentEntries.Select(entry =>
        {
            AuditEventDefinition definition = AuditEventDefinitions.Resolve(entry.Action);
            string actor = actors.TryGetValue(entry.ActorId, out UserSummary? user)
                ? user.DisplayName
                : "Former user";
            return new WorkspaceActivity(
                entry.Action,
                definition.Title,
                entry.SubjectDisplayName,
                actor,
                entry.OccurredAt);
        }).ToArray();

        return new WorkspaceOverview(
            moduleMetrics,
            activeMembers,
            pendingInvitations,
            activeRoles,
            membersWithAccess,
            eventsToday,
            recentActivity);
    }
}
