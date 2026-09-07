using System.Security.Claims;
using FlatpackApp.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FlatpackApp.Identity;

public sealed class PlatformClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IPlatformAccessDirectory platformAccess,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ClaimsIdentity identity = await base.GenerateClaimsAsync(user);
        IList<string> roleKeys = await UserManager.GetRolesAsync(user);
        PlatformRoleDefinition? role = null;
        foreach (string roleKey in roleKeys)
        {
            role = await platformAccess.FindRoleAsync(roleKey);
            if (role is not null) break;
        }
        if (role is null && user.IsPlatformAdministrator)
        {
            role = PlatformRoles.Find(PlatformRoles.Administrator);
        }

        if (role is not null && !user.IsPlatformAccessSuspended)
        {
            identity.AddClaim(new Claim("platform_access", "true"));
            identity.AddClaim(new Claim("platform_role", role.Key));
            foreach (string permission in role.Permissions)
            {
                identity.AddClaim(new Claim("platform_permission", permission));
            }
        }

        if (role?.Key == PlatformRoles.Administrator)
        {
            identity.AddClaim(new Claim("platform_admin", "true"));
        }

        return identity;
    }
}
