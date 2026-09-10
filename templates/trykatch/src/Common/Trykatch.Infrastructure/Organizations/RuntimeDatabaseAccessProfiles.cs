using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

public enum RuntimeDatabaseRoleKind
{
    Organization,
    Platform,
    Identity,
    Outbox
}

public sealed record RuntimeDatabaseRoles(
    string Organization,
    string Platform,
    string Identity,
    string Outbox)
{
    public IEnumerable<(RuntimeDatabaseRoleKind Kind, string Name)> All
    {
        get
        {
            yield return (RuntimeDatabaseRoleKind.Organization, Organization);
            yield return (RuntimeDatabaseRoleKind.Platform, Platform);
            yield return (RuntimeDatabaseRoleKind.Identity, Identity);
            yield return (RuntimeDatabaseRoleKind.Outbox, Outbox);
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
        "platform.organizations",
        "platform.memberships",
        "platform.roles",
        "platform.membership_roles",
        "platform.role_permissions",
        "platform.invitations",
        "platform.organization_data_placements",
        "platform.organization_creation_intents",
        "platform.audit_entries",
        "platform.outbox_messages",
        "platform.module_data_resources",
        "platform.module_migrations",
        "public.__EFMigrationsHistory",
        "identity.AspNetUsers",
        "identity.data_protection_keys",
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
        return kind switch
        {
            RuntimeDatabaseRoleKind.Organization => OrganizationPermissions(relation, resource),
            RuntimeDatabaseRoleKind.Platform => PlatformPermissions(relation, resource),
            RuntimeDatabaseRoleKind.Identity when relation.StartsWith("identity.", StringComparison.Ordinal) => Crud,
            RuntimeDatabaseRoleKind.Outbox when relation == "platform.outbox_messages" => SelectUpdate,
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
        "platform.memberships" or "platform.roles" or "platform.membership_roles"
            or "platform.role_permissions" or "platform.invitations" => Crud,
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
        _ when resource?.AccessRule == ModuleDataAccessRule.PlatformOnly => Crud,
        _ => None
    };

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Select = Set("SELECT");
    private static readonly IReadOnlySet<string> Insert = Set("INSERT");
    private static readonly IReadOnlySet<string> SelectInsert = Set("SELECT", "INSERT");
    private static readonly IReadOnlySet<string> SelectUpdate = Set("SELECT", "UPDATE");
    private static readonly IReadOnlySet<string> Crud = Set("SELECT", "INSERT", "UPDATE", "DELETE");
    private static readonly IReadOnlySet<string> SequenceUse = Set("USAGE", "SELECT", "UPDATE");

    private static HashSet<string> Set(params string[] permissions) =>
        new HashSet<string>(permissions, StringComparer.Ordinal);
}
