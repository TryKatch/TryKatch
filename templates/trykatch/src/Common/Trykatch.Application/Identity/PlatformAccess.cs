using System.Collections.Frozen;
using Trykatch.Application.Common;

namespace Trykatch.Application.Identity;

public static class PlatformPermissions
{
    public const string DashboardRead = "platform.dashboard.read";
    public const string TenantsRead = "platform.tenants.read";
    public const string TenantsManage = "platform.tenants.manage";
    public const string UsersRead = "platform.users.read";
    public const string UsersManage = "platform.users.manage";
    public const string InvitationsRead = "platform.invitations.read";
    public const string InvitationsManage = "platform.invitations.manage";
    public const string AuthenticationRead = "platform.authentication.read";
    public const string AuthenticationManage = "platform.authentication.manage";
    public const string OutboxRead = "platform.outbox.read";
    public const string OutboxReplay = "platform.outbox.replay";

    public static readonly IReadOnlyList<PlatformPermissionModuleDefinition> Modules =
    [
        new("dashboard", "Platform overview", "Platform status and operational summaries.",
        [
            new(DashboardRead, "View overview", "View platform health and summary information.", false)
        ]),
        new("tenants", "Tenant management", "Customer workspaces and their lifecycle.",
        [
            new(TenantsRead, "View tenants", "View the tenant directory and tenant details.", false),
            new(TenantsManage, "Manage tenants", "Create, update, activate, and deactivate tenants.", true)
        ]),
        new("users", "User management", "People who can operate the platform.",
        [
            new(UsersRead, "View platform users", "View people, invitations, roles, and access.", false),
            new(UsersManage, "Manage platform access", "Invite people and change platform roles.", true)
        ]),
        new("invitations", "Invitations", "Activation links for new platform users.",
        [
            new(InvitationsRead, "View invitations", "View pending platform invitations.", false),
            new(InvitationsManage, "Manage invitations", "Create, renew, and revoke activation links.", true)
        ]),
        new("authentication", "Authentication", "Platform sign-in and security configuration.",
        [
            new(AuthenticationRead, "View authentication", "View authentication policy and provider status.", false),
            new(AuthenticationManage, "Manage authentication", "Change security-sensitive authentication settings.", true)
        ]),
        new("outbox", "Outbox recovery", "Inspect terminal deliveries and request controlled replay.",
        [
            new(OutboxRead, "View outbox failures", "View bounded terminal delivery metadata.", false),
            new(OutboxReplay, "Replay outbox failures", "Request replay of a terminal delivery generation.", true)
        ])
    ];

    public static readonly IReadOnlySet<string> All = Modules
        .SelectMany(module => module.Permissions)
        .Select(permission => permission.Key)
        .ToFrozenSet(StringComparer.Ordinal);
}

public sealed record PlatformPermissionDefinition(string Key, string Name, string Description, bool IsSensitive, bool CanGrant = true);
public sealed record PlatformPermissionModuleDefinition(string Key, string Name, string Description, IReadOnlyList<PlatformPermissionDefinition> Permissions);

public sealed record PlatformRoleDefinition(
    string Key,
    string Name,
    string Description,
    int Order,
    IReadOnlySet<string> Permissions,
    bool IsSystem = true,
    bool CanAssign = true);

public static class PlatformRoles
{
    public const string Administrator = "platform-administrator";
    public const string Operator = "platform-operator";
    public const string Auditor = "platform-auditor";

    public static readonly IReadOnlyList<PlatformRoleDefinition> All =
    [
        new(Administrator, "Administrator", "Full platform control, including access and authentication policy.", 10, PlatformPermissions.All),
        new(Operator, "Operator", "Operate tenants and invitations without changing privileged platform access.", 20, new[]
        {
            PlatformPermissions.DashboardRead,
            PlatformPermissions.TenantsRead,
            PlatformPermissions.TenantsManage,
            PlatformPermissions.UsersRead,
            PlatformPermissions.InvitationsRead,
            PlatformPermissions.InvitationsManage,
            PlatformPermissions.AuthenticationRead,
            PlatformPermissions.OutboxRead
        }.ToFrozenSet(StringComparer.Ordinal)),
        new(Auditor, "Auditor", "Read-only visibility across platform operations and security posture.", 30, new[]
        {
            PlatformPermissions.DashboardRead,
            PlatformPermissions.TenantsRead,
            PlatformPermissions.UsersRead,
            PlatformPermissions.InvitationsRead,
            PlatformPermissions.AuthenticationRead,
            PlatformPermissions.OutboxRead
        }.ToFrozenSet(StringComparer.Ordinal))
    ];

    public static PlatformRoleDefinition? Find(string? key) =>
        All.SingleOrDefault(role => string.Equals(role.Key, key, StringComparison.Ordinal));

    public static bool Contains(string key) => Find(key) is not null;
}

public static class PlatformAccessRules
{
    public static bool CanGrant(IEnumerable<string> permissions, IReadOnlySet<string> grantBoundary) =>
        permissions.All(grantBoundary.Contains);

    public static bool CanAssign(PlatformRoleDefinition role, IReadOnlySet<string> grantBoundary) =>
        CanGrant(role.Permissions, grantBoundary);
}

public sealed record PlatformAccessUser(
    Guid Id,
    string Email,
    string DisplayName,
    string RoleKey,
    string RoleName,
    bool IsActive,
    bool IsPendingActivation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignedInAt);

public sealed record GrantPlatformAccessCommand(string Email, string DisplayName, string RoleKey);
public sealed record PlatformAccessGrant(PlatformAccessUser User, string? ActivationToken);
public sealed record ActivatePlatformAccessCommand(Guid UserId, string Token, string Password);
public sealed record SavePlatformRoleCommand(string Name, string Description, IReadOnlyCollection<string> Permissions);
public sealed record EffectivePlatformAccess(
    bool IsActive,
    bool IsAdministrator,
    string? RoleKey,
    IReadOnlySet<string> Permissions)
{
    public static EffectivePlatformAccess None { get; } = new(false, false, null, FrozenSet<string>.Empty);

    public bool HasPermission(string permission) =>
        IsActive && (IsAdministrator || Permissions.Contains(permission));
}

public interface IPlatformAccessDirectory
{
    Task<IReadOnlyList<PlatformRoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default);
    Task<PlatformRoleDefinition?> FindRoleAsync(string roleKey, CancellationToken cancellationToken = default);
    Task<Result<PlatformRoleDefinition>> CreateRoleAsync(Guid actorId, SavePlatformRoleCommand command, CancellationToken cancellationToken = default);
    Task<Result<PlatformRoleDefinition>> UpdateRoleAsync(Guid actorId, string roleKey, SavePlatformRoleCommand command, CancellationToken cancellationToken = default);
    Task<Result<bool>> DeleteRoleAsync(Guid actorId, string roleKey, CancellationToken cancellationToken = default);
    Task<PagedResult<PlatformAccessUser>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken = default);
    Task<Result<PlatformAccessUser>> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Result<PlatformAccessGrant>> GrantAsync(Guid actorId, GrantPlatformAccessCommand command, CancellationToken cancellationToken = default);
    Task<Result<PlatformAccessUser>> ChangeRoleAsync(Guid actorId, Guid userId, string roleKey, CancellationToken cancellationToken = default);
    Task<Result<PlatformAccessUser>> SetStatusAsync(Guid actorId, Guid userId, bool isActive, CancellationToken cancellationToken = default);
    Task<Result<string>> CreateActivationTokenAsync(Guid actorId, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<bool>> RevokeAsync(Guid actorId, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<bool>> ActivateAsync(ActivatePlatformAccessCommand command, CancellationToken cancellationToken = default);
    Task<EffectivePlatformAccess> ResolveEffectiveAccessAsync(Guid userId, CancellationToken cancellationToken = default);
}
