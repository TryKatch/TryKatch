using FlatpackApp.Application.Identity;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Domain.Organizations;
using FlatpackApp.Identity;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlatpackApp.Api.Development;

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
        IOrganizationDirectory organizations = scope.ServiceProvider.GetRequiredService<IOrganizationDirectory>();

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
        await platform.Database.ExecuteSqlRawAsync("SELECT set_config('app.platform_admin', 'true', true)");

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
            ownerRoleId = await platform.Roles
                .Where(x => x.OrganizationId == organization.Id && x.Name == "Owner" && x.IsSystem)
                .Select(x => x.Id)
                .SingleAsync();
        }

        Membership? membership = await platform.Memberships
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(x =>
                x.OrganizationId == organization.Id
                && x.UserId == tenantAdministrator.Id);
        if (membership is null)
        {
            membership = Membership.Create(organization.Id, tenantAdministrator.Id);
            membership.AssignRole(ownerRoleId);
            await organizations.AddMembershipAsync(membership, CancellationToken.None);
        }
        else
        {
            membership.Activate();
            membership.AssignRole(ownerRoleId);
        }

        await organizations.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync();
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
