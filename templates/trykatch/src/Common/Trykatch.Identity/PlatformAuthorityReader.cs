using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Identity;

namespace Trykatch.Identity;

internal sealed class PlatformAuthorityReader(IdentityDbContext database) : IPlatformAuthorityReader
{
    public Task<bool> HasOtherActiveAdministratorAsync(Guid exceptUserId, CancellationToken cancellationToken) =>
        (from user in database.Users.AsNoTracking()
         join link in database.UserRoles.AsNoTracking() on user.Id equals link.UserId
         join role in database.Roles.AsNoTracking() on link.RoleId equals role.Id
         where role.Name == PlatformRoles.Administrator && !user.IsPlatformAccessSuspended
             && user.EmailConfirmed && user.PasswordHash != null && user.Id != exceptUserId
         select user.Id).AnyAsync(cancellationToken);

    public async Task<EffectivePlatformAccess> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (user is null) return EffectivePlatformAccess.None;

        var assigned = await (
            from link in database.UserRoles.AsNoTracking()
            join role in database.Roles.AsNoTracking() on link.RoleId equals role.Id
            where link.UserId == userId && role.Name != null
            select new { role.Id, role.Name }).ToArrayAsync(cancellationToken);
        var platformRoles = assigned.Where(role => PlatformRoles.Contains(role.Name!)
            || role.Name!.StartsWith("platform-custom-", StringComparison.Ordinal)).ToArray();
        if (platformRoles.Length == 0) return EffectivePlatformAccess.None;

        Guid[] customIds = platformRoles.Where(role => !PlatformRoles.Contains(role.Name!)).Select(role => role.Id).ToArray();
        string[] customPermissions = await database.RoleClaims.AsNoTracking()
            .Where(claim => customIds.Contains(claim.RoleId) && claim.ClaimType == "platform_permission" && claim.ClaimValue != null)
            .Select(claim => claim.ClaimValue!).ToArrayAsync(cancellationToken);
        bool isAdministrator = platformRoles.Any(role => role.Name == PlatformRoles.Administrator);
        IReadOnlySet<string> permissions = platformRoles
            .SelectMany(role => PlatformRoles.Find(role.Name)?.Permissions ?? FrozenSet<string>.Empty)
            .Concat(customPermissions.Where(PlatformPermissions.All.Contains))
            .ToFrozenSet(StringComparer.Ordinal);
        return new EffectivePlatformAccess(
            !user.IsPlatformAccessSuspended && user.EmailConfirmed && user.PasswordHash is not null,
            isAdministrator,
            isAdministrator ? PlatformRoles.Administrator : platformRoles[0].Name,
            permissions);
    }
}
