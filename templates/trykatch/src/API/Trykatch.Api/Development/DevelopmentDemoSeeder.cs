using Trykatch.Application.Authorization;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Identity;
using Trykatch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Trykatch.Api.Development;

internal static class DevelopmentDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("DevelopmentDemo:Enabled"))
        {
            return;
        }

        string platformEmail = Required(configuration, "DevelopmentDemo:PlatformAdminEmail");
        string tenantEmail = Required(configuration, "DevelopmentDemo:TenantAdminEmail");
        string password = Required(configuration, "DevelopmentDemo:Password");
        string organizationName = configuration["DevelopmentDemo:OrganizationName"] ?? "Demo Workspace";
        string organizationSlug = configuration["DevelopmentDemo:OrganizationSlug"] ?? "demo-workspace";

        using IServiceScope scope = services.CreateScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        PlatformDbContext platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        OrganizationControlPlaneDbContext organizationData = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        IOrganizationDirectory organizations = scope.ServiceProvider.GetRequiredService<IOrganizationDirectory>();
        IOrganizationDataPlacement placement = scope.ServiceProvider.GetRequiredService<IOrganizationDataPlacement>();
        IPermissionCatalog permissionCatalog = scope.ServiceProvider.GetRequiredService<IPermissionCatalog>();

        ApplicationUser platformAdministrator = await EnsureUserAsync(
            users,
            platformEmail,
            "Platform Administrator",
            password,
            isPlatformAdministrator: true);
        await EnsurePlatformAdministratorRoleAsync(users, platformAdministrator);

        ApplicationUser tenantAdministrator = await EnsureUserAsync(
            users,
            tenantEmail,
            "Tenant Administrator",
            password,
            isPlatformAdministrator: false);

        await using IDbContextTransaction transaction = await platform.Database.BeginTransactionAsync();

        Organization? organization = await platform.Organizations.SingleOrDefaultAsync(x => x.Slug == organizationSlug);
        Guid ownerRoleId;
        if (organization is null)
        {
            organization = Organization.Create(organizationName, organizationSlug);
            await organizations.AddAsync(organization, CancellationToken.None);
            OrganizationRoleSeeds roles = await organizations.SeedRolesAsync(organization.Id, CancellationToken.None);
            ownerRoleId = roles.OwnerRoleId;
        }
        else
        {
            ownerRoleId = await organizations.GetOwnerRoleIdAsync(organization.Id, CancellationToken.None);
        }

        OrganizationDataPlacementResult placementResult = await placement.ProvisionAsync(
            new(organization.Id, OrganizationDataPlacementKind.Shared), CancellationToken.None);
        if (!placementResult.IsReady)
            throw new InvalidOperationException("Development demo organization data placement is not ready.");

        await transaction.CommitAsync();

        await using IDbContextTransaction organizationTransaction = await organizationData.Database.BeginTransactionAsync();
        await organizationData.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organization.Id.ToString()}, true), set_config('app.actor_id', {tenantAdministrator.Id.ToString()}, true)");

        Role[] systemRoles = await organizationData.Roles
            .Include(x => x.Permissions)
            .Where(x => x.OrganizationId == organization.Id && x.IsSystem)
            .ToArrayAsync();
        foreach (Role role in systemRoles)
        {
            IReadOnlySet<string> defaults = role.Name switch
            {
                "Owner" => permissionCatalog.Keys,
                "Admin" => permissionCatalog.GetDefaultsForRole(DefaultOrganizationRoles.Admin),
                "Member" => permissionCatalog.GetDefaultsForRole(DefaultOrganizationRoles.Member),
                "Viewer" => permissionCatalog.GetDefaultsForRole(DefaultOrganizationRoles.Viewer),
                _ => Array.Empty<string>().ToHashSet(StringComparer.Ordinal)
            };
            role.AddMissingPermissions(defaults);
        }

        Membership? membership = await organizationData.Memberships
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(x =>
                x.OrganizationId == organization.Id
                && x.UserId == tenantAdministrator.Id);
        if (membership is null)
        {
            membership = Membership.Create(organization.Id, tenantAdministrator.Id);
            membership.AssignRole(ownerRoleId);
            await organizationData.Memberships.AddAsync(membership, CancellationToken.None);
        }
        else
        {
            membership.Activate();
            membership.AssignRole(ownerRoleId);
        }

        await organizationData.SaveChangesAsync(CancellationToken.None);
        await organizationTransaction.CommitAsync();
    }

    private static async Task<ApplicationUser> EnsureUserAsync(
        UserManager<ApplicationUser> users,
        string email,
        string displayName,
        string password,
        bool isPlatformAdministrator)
    {
        ApplicationUser? user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                IsPlatformAdministrator = isPlatformAdministrator,
                IsPlatformAccessSuspended = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            EnsureSucceeded(await users.CreateAsync(user, password));
            return user;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            string resetToken = await users.GeneratePasswordResetTokenAsync(user);
            EnsureSucceeded(await users.ResetPasswordAsync(user, resetToken, password));
        }

        bool changed = false;
        if (!string.Equals(user.DisplayName, displayName, StringComparison.Ordinal))
        {
            user.DisplayName = displayName;
            changed = true;
        }

        if (user.IsPlatformAdministrator != isPlatformAdministrator || user.IsPlatformAccessSuspended)
        {
            user.IsPlatformAdministrator = isPlatformAdministrator;
            user.IsPlatformAccessSuspended = false;
            changed = true;
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            changed = true;
        }

        if (changed)
        {
            EnsureSucceeded(await users.UpdateAsync(user));
        }

        return user;
    }

    private static async Task EnsurePlatformAdministratorRoleAsync(
        UserManager<ApplicationUser> users,
        ApplicationUser administrator)
    {
        if (!await users.IsInRoleAsync(administrator, PlatformRoles.Administrator))
        {
            EnsureSucceeded(await users.AddToRoleAsync(administrator, PlatformRoles.Administrator));
        }
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Development demo setting '{key}' is required when demo seeding is enabled.");

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }
    }
}
