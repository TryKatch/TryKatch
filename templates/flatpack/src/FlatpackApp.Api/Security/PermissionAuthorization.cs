using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Identity;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

namespace FlatpackApp.Api.Security;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;
public sealed record PlatformPermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler(IOrganizationContext organizationContext, IPermissionCatalog permissionCatalog)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (permissionCatalog.Contains(requirement.Permission)
            && organizationContext.IsResolved
            && organizationContext.Permissions.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class PlatformPermissionAuthorizationHandler
    : AuthorizationHandler<PlatformPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformPermissionRequirement requirement)
    {
        if (PlatformPermissions.All.Contains(requirement.Permission)
            && (context.User.HasClaim("platform_permission", requirement.Permission)
                || context.User.HasClaim("platform_admin", "true")))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    internal const string PolicyPrefix = "permission:";

    public RequirePermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Policy = PolicyPrefix + permission;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePlatformPermissionAttribute : AuthorizeAttribute
{
    internal const string PolicyPrefix = "platform-permission:";

    public RequirePlatformPermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Policy = PolicyPrefix + permission;
    }
}

public sealed class PermissionPolicyProvider(
    IOptions<AuthorizationOptions> options,
    IPermissionCatalog permissionCatalog) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequirePlatformPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            string platformPermission = policyName[RequirePlatformPermissionAttribute.PolicyPrefix.Length..];
            AuthorizationPolicyBuilder platformBuilder = CreateBuilder();
            if (PlatformPermissions.All.Contains(platformPermission))
                platformBuilder.AddRequirements(new PlatformPermissionRequirement(platformPermission));
            else
                platformBuilder.RequireAssertion(_ => false);
            return Task.FromResult<AuthorizationPolicy?>(platformBuilder.Build());
        }

        if (!policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal)) return fallback.GetPolicyAsync(policyName);

        string permission = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
        AuthorizationPolicyBuilder builder = CreateBuilder();
        if (permissionCatalog.Contains(permission))
            builder.AddRequirements(new PermissionRequirement(permission));
        else
            builder.RequireAssertion(_ => false);

        return Task.FromResult<AuthorizationPolicy?>(builder.Build());
    }

    private static AuthorizationPolicyBuilder CreateBuilder()
    {
        AuthorizationPolicyBuilder builder = new(
            FlatpackAuthenticationSchemes.ApplicationCookie,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        builder.RequireAuthenticatedUser();
        return builder;
    }
}
