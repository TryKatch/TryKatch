using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace FlatpackApp.Application.Authorization;

public static class Permissions
{
    public const string OrganizationsRead = "organizations.read";
    public const string OrganizationsManage = "organizations.manage";
    public const string MembersRead = "members.read";
    public const string MembersManage = "members.manage";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string ProjectsRead = "projects.read";
    public const string ProjectsManage = "projects.manage";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlySet<string> All = new[]
    {
        OrganizationsRead,
        OrganizationsManage,
        MembersRead,
        MembersManage,
        RolesRead,
        RolesManage,
        ProjectsRead,
        ProjectsManage,
        AuditRead
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// Stable role keys used only for module-owned default grants during organization setup.
/// Runtime authorization continues to evaluate permission keys, never role names.
/// </summary>
public static class DefaultOrganizationRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Viewer = "viewer";

    public static readonly IReadOnlySet<string> All = new[]
    {
        Owner,
        Admin,
        Member,
        Viewer
    }.ToFrozenSet(StringComparer.Ordinal);
}

public sealed record PermissionDefinition(
    string Key,
    string Name,
    string Description,
    bool IsSensitive = false,
    int Order = 0,
    IReadOnlyList<string>? DefaultRoles = null);

public sealed record PermissionModuleDefinition(
    string Key,
    string Name,
    string Description,
    int Order,
    IReadOnlyList<PermissionDefinition> Permissions);

/// <summary>
/// A bounded application module contributes permission metadata through this
/// interface. The central catalog aggregates explicitly registered providers;
/// callers never need runtime assembly scanning or UI-specific permission lists.
/// </summary>
public interface IPermissionDefinitionProvider
{
    IReadOnlyList<PermissionModuleDefinition> GetModules();
}

public interface IPermissionCatalog
{
    IReadOnlyList<PermissionModuleDefinition> Modules { get; }
    IReadOnlySet<string> Keys { get; }
    bool Contains(string permission);
    IReadOnlySet<string> GetDefaultsForRole(string roleKey);
}

public sealed class PermissionCatalog : IPermissionCatalog
{
    private static readonly Regex StableKey = new(
        "^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public PermissionCatalog(IEnumerable<IPermissionDefinitionProvider> providers)
    {
        PermissionModuleDefinition[] modules = providers
            .SelectMany(provider => provider.GetModules())
            .OrderBy(module => module.Order)
            .ThenBy(module => module.Key, StringComparer.Ordinal)
            .Select(module => module with
            {
                Permissions = module.Permissions
                    .OrderBy(permission => permission.Order)
                    .ThenBy(permission => permission.Key, StringComparer.Ordinal)
                    .ToArray()
            })
            .ToArray();

        if (modules.Length == 0)
            throw new InvalidOperationException("At least one permission module must be registered.");

        EnsureUnique(modules.Select(module => module.Key), "permission module");
        PermissionDefinition[] permissions = modules.SelectMany(module => module.Permissions).ToArray();
        EnsureUnique(permissions.Select(permission => permission.Key), "permission");

        foreach (PermissionModuleDefinition module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Key) || string.IsNullOrWhiteSpace(module.Name) || module.Permissions.Count == 0)
                throw new InvalidOperationException("Permission modules require a stable key, display name, and at least one permission.");
        }

        foreach (PermissionDefinition permission in permissions)
        {
            if (permission.Key.Length > 120 || !StableKey.IsMatch(permission.Key))
                throw new InvalidOperationException($"Permission '{permission.Key}' must use a stable lower-case resource.action key no longer than 120 characters.");
            if (string.IsNullOrWhiteSpace(permission.Name) || string.IsNullOrWhiteSpace(permission.Description))
                throw new InvalidOperationException($"Permission '{permission.Key}' requires user-facing metadata.");

            string? unknownRole = permission.DefaultRoles?.FirstOrDefault(role => !DefaultOrganizationRoles.All.Contains(role));
            if (unknownRole is not null)
                throw new InvalidOperationException($"Permission '{permission.Key}' declares unknown default role '{unknownRole}'.");
        }

        Modules = modules;
        Keys = permissions.Select(permission => permission.Key).ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<PermissionModuleDefinition> Modules { get; }
    public IReadOnlySet<string> Keys { get; }
    public bool Contains(string permission) => Keys.Contains(permission);
    public IReadOnlySet<string> GetDefaultsForRole(string roleKey)
    {
        if (!DefaultOrganizationRoles.All.Contains(roleKey))
            throw new ArgumentOutOfRangeException(nameof(roleKey), roleKey, "Unknown organization role key.");

        return Modules
            .SelectMany(module => module.Permissions)
            .Where(permission => permission.DefaultRoles?.Contains(roleKey, StringComparer.Ordinal) == true)
            .Select(permission => permission.Key)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    private static void EnsureUnique(IEnumerable<string> values, string subject)
    {
        string? duplicate = values
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate {subject} key '{duplicate}'.");
    }
}

public sealed class BuiltInPermissionDefinitionProvider : IPermissionDefinitionProvider
{
    private static readonly IReadOnlyList<PermissionModuleDefinition> Modules =
    [
        new("organization", "Organization", "Workspace identity, settings, and lifecycle.", 10,
        [
            new(Permissions.OrganizationsRead, "View organization", "View organization details and configuration.", Order: 10,
                DefaultRoles: [DefaultOrganizationRoles.Admin]),
            new(Permissions.OrganizationsManage, "Manage organization", "Change organization settings and lifecycle.", IsSensitive: true, Order: 20)
        ]),
        new("people", "People and access", "Memberships, invitations, roles, and access policy.", 20,
        [
            new(Permissions.MembersRead, "View people", "View members, invitations, and their assigned roles.", Order: 10,
                DefaultRoles: [DefaultOrganizationRoles.Admin, DefaultOrganizationRoles.Member, DefaultOrganizationRoles.Viewer]),
            new(Permissions.MembersManage, "Manage people", "Invite, edit, suspend, archive, restore, and request deletion of organization access.", IsSensitive: true, Order: 20,
                DefaultRoles: [DefaultOrganizationRoles.Admin]),
            new(Permissions.RolesRead, "View roles", "View system and custom role definitions.", Order: 30,
                DefaultRoles: [DefaultOrganizationRoles.Admin, DefaultOrganizationRoles.Member, DefaultOrganizationRoles.Viewer]),
            new(Permissions.RolesManage, "Manage roles", "Create, change, archive, restore, and request deletion of custom roles and permission grants.", IsSensitive: true, Order: 40,
                DefaultRoles: [DefaultOrganizationRoles.Admin])
        ]),
        new("security", "Security and audit", "Security activity and accountability records.", 40,
        [
            new(Permissions.AuditRead, "View audit activity", "View security-sensitive actions and their actors.", IsSensitive: true, Order: 10,
                DefaultRoles: [DefaultOrganizationRoles.Admin])
        ])
    ];

    public IReadOnlyList<PermissionModuleDefinition> GetModules() => Modules;
}
