using System.Security.Claims;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlatpackApp.Api.Security;

public sealed class PlatformDataTransactionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        PlatformDbContext platformDbContext,
        IWorkspaceContextCookie workspaceCookie)
    {
        Endpoint? endpoint = context.GetEndpoint();
        bool usesPlatformData = endpoint?.Metadata.GetMetadata<PlatformDataScopedAttribute>() is not null
            || endpoint?.Metadata.GetMetadata<OrganizationScopedAttribute>() is not null;
        if (!usesPlatformData || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        string? subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out Guid actorId))
        {
            await next(context);
            return;
        }

        await using IDbContextTransaction transaction = await platformDbContext.Database.BeginTransactionAsync(context.RequestAborted);
        string actor = actorId.ToString();
        string platformAdministrator = context.User.HasClaim("platform_admin", "true") ? "true" : "false";
        Guid organizationId;
        string? organizationClaim = context.User.FindFirstValue("organization_id");
        bool hasOrganization = Guid.TryParse(organizationClaim, out organizationId)
            || workspaceCookie.TryRead(context, out organizationId);
        string organization = hasOrganization ? organizationId.ToString() : string.Empty;
        await platformDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.actor_id', {actor}, true), set_config('app.organization_id', {organization}, true), set_config('app.platform_admin', {platformAdministrator}, true)",
            context.RequestAborted);

        await next(context);
        if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
        {
            await transaction.CommitAsync(context.RequestAborted);
        }
    }
}
