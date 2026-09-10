using Trykatch.Application.Common;
using Trykatch.Domain.Organizations;

namespace Trykatch.Application.Auditing;

public static class AuditActions
{
    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectArchived = "project.archived";
    public const string ProjectRestored = "project.restored";
    public const string ProjectDeleted = "project.deleted";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleArchived = "role.archived";
    public const string RoleRestored = "role.restored";
    public const string RoleDeleted = "role.deleted";
    public const string MembershipUpdated = "membership.updated";
    public const string MembershipArchived = "membership.archived";
    public const string MembershipRestored = "membership.restored";
    public const string MembershipDeleted = "membership.deleted";
    public const string InvitationCreated = "invitation.created";
    public const string InvitationUpdated = "invitation.updated";
    public const string InvitationRevoked = "invitation.revoked";
    public const string InvitationRestored = "invitation.restored";
    public const string InvitationDeleted = "invitation.deleted";
}

public sealed record AuditTarget(string Type, string Id, string DisplayName);

public sealed record AuditQuery(
    int Page = 1,
    int PageSize = 50,
    string? Search = null,
    string? Action = null,
    string? SubjectType = null,
    Guid? ActorId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string SortBy = "occurredAt",
    string SortDirection = "desc");

public sealed record AuditReadPage(
    PagedResult<AuditEntry> Page,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> SubjectTypes,
    IReadOnlyList<Guid> ActorIds);

public interface IAuditReader
{
    Task<AuditReadPage> ListAsync(Guid organizationId, AuditQuery query, CancellationToken cancellationToken);
}

public interface IAuditWriter
{
    // Details must contain non-secret, display-safe values only. Audit records are immutable snapshots.
    void Record(string action, AuditTarget target, IReadOnlyDictionary<string, string?>? details = null);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record AuditEventDefinition(string Action, string Title, string Category, string DescriptionTemplate, string Severity = "Information");

public static class AuditEventDefinitions
{
    private static readonly Dictionary<string, AuditEventDefinition> Definitions = new(StringComparer.Ordinal)
    {
        [AuditActions.ProjectCreated] = new(AuditActions.ProjectCreated, "Project created", "Projects", "{actor} created {target}."),
        [AuditActions.ProjectUpdated] = new(AuditActions.ProjectUpdated, "Project updated", "Projects", "{actor} updated {target}."),
        [AuditActions.ProjectArchived] = new(AuditActions.ProjectArchived, "Project archived", "Projects", "{actor} archived {target}.", "Warning"),
        [AuditActions.ProjectRestored] = new(AuditActions.ProjectRestored, "Project restored", "Projects", "{actor} restored {target}."),
        [AuditActions.ProjectDeleted] = new(AuditActions.ProjectDeleted, "Project pending deletion", "Projects", "{actor} requested deletion of {target}.", "Warning"),
        [AuditActions.RoleCreated] = new(AuditActions.RoleCreated, "Role created", "Access", "{actor} created {target}."),
        [AuditActions.RoleUpdated] = new(AuditActions.RoleUpdated, "Role updated", "Access", "{actor} changed permissions for {target}.", "Warning"),
        [AuditActions.RoleArchived] = new(AuditActions.RoleArchived, "Role archived", "Access", "{actor} archived {target}.", "Warning"),
        [AuditActions.RoleRestored] = new(AuditActions.RoleRestored, "Role restored", "Access", "{actor} restored {target}."),
        [AuditActions.RoleDeleted] = new(AuditActions.RoleDeleted, "Role pending deletion", "Access", "{actor} requested deletion of {target}.", "Warning"),
        [AuditActions.MembershipUpdated] = new(AuditActions.MembershipUpdated, "Member access updated", "Access", "{actor} changed access for {target}.", "Warning"),
        [AuditActions.MembershipArchived] = new(AuditActions.MembershipArchived, "Member archived", "People", "{actor} archived {target}.", "Warning"),
        [AuditActions.MembershipRestored] = new(AuditActions.MembershipRestored, "Member restored", "People", "{actor} restored {target}."),
        [AuditActions.MembershipDeleted] = new(AuditActions.MembershipDeleted, "Member pending deletion", "People", "{actor} requested deletion of {target}.", "Warning"),
        [AuditActions.InvitationCreated] = new(AuditActions.InvitationCreated, "Invitation sent", "People", "{actor} invited {target}."),
        [AuditActions.InvitationUpdated] = new(AuditActions.InvitationUpdated, "Invitation updated", "People", "{actor} changed the expiry for {target}."),
        [AuditActions.InvitationRevoked] = new(AuditActions.InvitationRevoked, "Invitation revoked", "People", "{actor} revoked the invitation for {target}.", "Warning"),
        [AuditActions.InvitationRestored] = new(AuditActions.InvitationRestored, "Invitation restored", "People", "{actor} restored {target}."),
        [AuditActions.InvitationDeleted] = new(AuditActions.InvitationDeleted, "Invitation pending deletion", "People", "{actor} requested deletion of {target}.", "Warning")
    };

    public static AuditEventDefinition Resolve(string action)
    {
        if (Definitions.TryGetValue(action, out AuditEventDefinition? definition)) return definition;

        string[] parts = action.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string category = parts.Length > 0 ? Humanize(parts[0]) : "System";
        string title = parts.Length > 1 ? $"{category} {Humanize(parts[^1]).ToLowerInvariant()}" : Humanize(action);
        return new(action, title, category, "{actor} performed an action on {target}.");
    }

    public static IReadOnlyList<AuditEventDefinition> Describe(IEnumerable<string> actions) =>
        actions.Distinct(StringComparer.Ordinal).Select(Resolve).OrderBy(x => x.Category).ThenBy(x => x.Title).ToArray();

    private static string Humanize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' ').Replace('-', ' ');
}
