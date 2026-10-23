using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

public enum RuntimeDatabaseRoleKind
{
    Organization,
    Platform,
    Identity,
    Outbox,
    WorkCoordinator
}

public sealed record RuntimeDatabaseRoles(
    string Organization,
    string Platform,
    string Identity,
    string Outbox,
    string WorkCoordinator = "trykatch_work_coordinator")
{
    public IEnumerable<(RuntimeDatabaseRoleKind Kind, string Name)> All
    {
        get
        {
            yield return (RuntimeDatabaseRoleKind.Organization, Organization);
            yield return (RuntimeDatabaseRoleKind.Platform, Platform);
            yield return (RuntimeDatabaseRoleKind.Identity, Identity);
            yield return (RuntimeDatabaseRoleKind.Outbox, Outbox);
            yield return (RuntimeDatabaseRoleKind.WorkCoordinator, WorkCoordinator);
        }
    }
}

/// <summary>
/// The single ownership-to-effective-access mapping used by both provisioning
/// and PostgreSQL catalog inspection. Absence from a profile means no access.
/// </summary>
public static class RuntimeDatabaseAccessProfiles
{
    public static IReadOnlySet<string> ManagedSchemas { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "app", "platform", "identity", "reference", "infrastructure"
    };

    public static IReadOnlySet<string> HostOwnedRelations { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "app.module_event_inbox",
        "app.module_work_completions",
        "infrastructure.module_work_items",
        "infrastructure.module_event_deliveries",
        "platform.organizations",
        "platform.organization_assistant_settings",
        "platform.memberships",
        "platform.roles",
        "platform.membership_roles",
        "platform.role_permissions",
        "platform.invitations",
        "platform.organization_data_placements",
        "platform.organization_creation_intents",
        "platform.audit_intents",
        "platform.audit_entries",
        "platform.outbox_messages",
        "platform.outbox_replay_requests",
        "platform.outbox_recovery_events",
        "platform.module_data_resources",
        "platform.module_migrations",
        "public.__EFMigrationsHistory",
        "identity.AspNetUsers",
        "identity.data_protection_keys",
        "identity.recent_assurance_grants",
        "identity.pending_mfa_enrollments",
        "identity.account_security_events",
        "identity.AspNetRoles",
        "identity.AspNetRoleClaims",
        "identity.AspNetUserClaims",
        "identity.AspNetUserLogins",
        "identity.AspNetUserRoles",
        "identity.AspNetUserTokens",
        "identity.OpenIddictApplications",
        "identity.OpenIddictAuthorizations",
        "identity.OpenIddictScopes",
        "identity.OpenIddictTokens"
    };

    public static IReadOnlySet<string> PermissionsFor(
        RuntimeDatabaseRoleKind kind,
        string relation,
        IReadOnlyDictionary<string, DataResourceDescriptor> resources)
    {
        DataResourceDescriptor? resource = resources.GetValueOrDefault(relation);
        if (OperationalDataResources.IsReserved(relation))
        {
            if (resource is null || !OperationalDataResources.All.Any(expected =>
                    expected.Schema == resource.Schema && expected.Table == resource.Table
                    && expected.Ownership == resource.Ownership && expected.AccessRule == resource.AccessRule
                    && expected.IsolationPolicy == resource.IsolationPolicy))
                return None;
            return (kind, relation) switch
            {
                (RuntimeDatabaseRoleKind.Organization, "infrastructure.module_work_items") => Insert,
                (RuntimeDatabaseRoleKind.Organization, "app.module_event_inbox") => SelectInsert,
                (RuntimeDatabaseRoleKind.Organization, "app.module_work_completions") => SelectInsert,
                (RuntimeDatabaseRoleKind.Outbox, "infrastructure.module_event_deliveries") => SelectInsert,
                (RuntimeDatabaseRoleKind.WorkCoordinator, "infrastructure.module_work_items") => SelectInsertUpdate,
                (RuntimeDatabaseRoleKind.WorkCoordinator, "infrastructure.module_event_deliveries") => SelectUpdate,
                _ => None
            };
        }
        return kind switch
        {
            RuntimeDatabaseRoleKind.Organization => OrganizationPermissions(relation, resource),
            RuntimeDatabaseRoleKind.Platform => PlatformPermissions(relation, resource),
            RuntimeDatabaseRoleKind.Identity when relation == "identity.account_security_events" => SelectInsert,
            RuntimeDatabaseRoleKind.Identity when relation.StartsWith("identity.", StringComparison.Ordinal) => Crud,
            RuntimeDatabaseRoleKind.Outbox when relation == "platform.outbox_messages" => SelectUpdate,
            RuntimeDatabaseRoleKind.Outbox when relation == "platform.audit_intents" => Select,
            RuntimeDatabaseRoleKind.Outbox when relation == "platform.audit_entries" => SelectInsert,
            RuntimeDatabaseRoleKind.Outbox when relation == "platform.outbox_replay_requests" => Select,
            RuntimeDatabaseRoleKind.Outbox when relation == "platform.outbox_recovery_events" => SelectInsert,
            _ => None
        };
    }

    public static IReadOnlySet<string> SequencePermissionsFor(
        RuntimeDatabaseRoleKind kind,
        string sequence) =>
        kind == RuntimeDatabaseRoleKind.Identity
        && sequence.StartsWith("identity.", StringComparison.Ordinal)
            ? SequenceUse
            : None;

    private static IReadOnlySet<string> OrganizationPermissions(
        string relation, DataResourceDescriptor? resource) => relation switch
        {
            "platform.organizations" or "platform.module_data_resources" => Select,
            "platform.organization_assistant_settings" => SelectInsertUpdate,
            "platform.memberships" or "platform.roles" or "platform.membership_roles"
                or "platform.role_permissions" or "platform.invitations" => Crud,
            "platform.audit_intents" => Insert,
            "platform.audit_entries" => SelectInsert,
            "platform.outbox_messages" => Insert,
            _ when resource?.Ownership == ModuleDataOwnership.Organization => Crud,
            _ when resource?.AccessRule == ModuleDataAccessRule.GlobalReadOnly => Select,
            _ => None
        };

    private static IReadOnlySet<string> PlatformPermissions(
        string relation, DataResourceDescriptor? resource) => relation switch
        {
            "platform.organizations" or "platform.organization_data_placements"
                or "platform.organization_creation_intents" => Crud,
            "platform.roles" or "platform.role_permissions" or "platform.invitations" => SelectInsert,
            "platform.outbox_replay_requests" => SelectInsert,
            "platform.outbox_recovery_events" => Select,
            _ when resource?.AccessRule == ModuleDataAccessRule.PlatformOnly => Crud,
            _ => None
        };

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Select = Set("SELECT");
    private static readonly IReadOnlySet<string> Insert = Set("INSERT");
    private static readonly IReadOnlySet<string> SelectInsert = Set("SELECT", "INSERT");
    private static readonly IReadOnlySet<string> SelectUpdate = Set("SELECT", "UPDATE");
    private static readonly IReadOnlySet<string> SelectInsertUpdate = Set("SELECT", "INSERT", "UPDATE");
    private static readonly IReadOnlySet<string> Crud = Set("SELECT", "INSERT", "UPDATE", "DELETE");
    private static readonly IReadOnlySet<string> SequenceUse = Set("USAGE", "SELECT", "UPDATE");

    private static HashSet<string> Set(params string[] permissions) =>
        new HashSet<string>(permissions, StringComparer.Ordinal);
}
