using OpenIddict.Abstractions;
using FlatpackApp.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Identity;

public static class IdentitySeeder
{
    public static async Task SeedPlatformAdministratorAsync(IServiceProvider services, IConfiguration configuration)
    {
        string? email = configuration["Bootstrap:PlatformAdminEmail"];
        string? password = configuration["Bootstrap:PlatformAdminPassword"];
        using IServiceScope scope = services.CreateScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager<IdentityRole<Guid>> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (PlatformRoleDefinition definition in PlatformRoles.All)
        {
            if (await roles.RoleExistsAsync(definition.Key)) continue;
            IdentityResult roleResult = await roles.CreateAsync(new IdentityRole<Guid>(definition.Key) { Id = Guid.CreateVersion7() });
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(string.Join("; ", roleResult.Errors.Select(x => x.Description)));
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        ApplicationUser? user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                DisplayName = "Platform Administrator",
                IsPlatformAdministrator = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            IdentityResult result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
            }
        }

        if (!await users.IsInRoleAsync(user, PlatformRoles.Administrator))
        {
            user.IsPlatformAdministrator = true;
            user.IsPlatformAccessSuspended = false;
            IdentityResult updateResult = await users.UpdateAsync(user);
            if (!updateResult.Succeeded)
                throw new InvalidOperationException(string.Join("; ", updateResult.Errors.Select(x => x.Description)));
            IdentityResult assignmentResult = await users.AddToRoleAsync(user, PlatformRoles.Administrator);
            if (!assignmentResult.Succeeded)
                throw new InvalidOperationException(string.Join("; ", assignmentResult.Errors.Select(x => x.Description)));
        }
    }

    public static async Task SeedOpenIddictClientsAsync(IServiceProvider services, IConfiguration configuration)
    {
        using IServiceScope scope = services.CreateScope();
        IOpenIddictApplicationManager applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (IConfigurationSection client in configuration.GetSection("OpenIddict:Clients").GetChildren())
        {
            string? clientId = client["ClientId"];
            if (string.IsNullOrWhiteSpace(clientId) || await applications.FindByClientIdAsync(clientId) is not null)
            {
                continue;
            }

            string grantType = client["GrantType"] ?? "authorization_code";
            OpenIddictApplicationDescriptor descriptor = new()
            {
                ClientId = clientId,
                DisplayName = client["DisplayName"] ?? clientId
            };
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "flatpack-api");

            if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
            {
                descriptor.ClientType = OpenIddictConstants.ClientTypes.Confidential;
                descriptor.ClientSecret = client["ClientSecret"]
                    ?? throw new InvalidOperationException($"OpenIddict client '{clientId}' requires a secret.");
                descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
            }
            else if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                descriptor.ClientType = string.IsNullOrWhiteSpace(client["ClientSecret"])
                    ? OpenIddictConstants.ClientTypes.Public
                    : OpenIddictConstants.ClientTypes.Confidential;
                descriptor.ClientSecret = client["ClientSecret"];
                descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
                descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.EndSession);
                descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
                descriptor.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
                descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Profile);
                descriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
                foreach (string redirectUri in client.GetSection("RedirectUris").Get<string[]>() ?? [])
                {
                    descriptor.RedirectUris.Add(new Uri(redirectUri, UriKind.Absolute));
                }
                foreach (string redirectUri in client.GetSection("PostLogoutRedirectUris").Get<string[]>() ?? [])
                {
                    descriptor.PostLogoutRedirectUris.Add(new Uri(redirectUri, UriKind.Absolute));
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported OpenIddict grant type '{grantType}'.");
            }

            await applications.CreateAsync(descriptor);
        }
    }
}
