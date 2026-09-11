using System.Security.Claims;
using Trykatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Trykatch.Identity;

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
        EffectivePlatformAccess access = await platformAccess.ResolveEffectiveAccessAsync(user.Id);
        if (access.IsActive)
        {
            identity.AddClaim(new Claim("platform_access", "true"));
            identity.AddClaim(new Claim("platform_role", access.RoleKey!));
            foreach (string permission in access.Permissions)
            {
                identity.AddClaim(new Claim("platform_permission", permission));
            }

            if (access.IsAdministrator)
            {
                identity.AddClaim(new Claim("platform_admin", "true"));
            }
        }

        return identity;
    }
}
