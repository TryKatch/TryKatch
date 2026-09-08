using System.Security.Claims;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Infrastructure.Organizations;
using FlatpackApp.Modules.AspNetCore;

namespace FlatpackApp.Api.Security;

public sealed class OrganizationScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IWorkspaceContextCookie workspaceCookie,
        IOrganizationAccessResolver resolver,
        IOrganizationContextInitializer initializer)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IFlatpackOrganizationScopedMetadata>() is null)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Authentication required");
            return;
        }

        string? subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out Guid actorId))
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Authenticated subject is invalid");
            return;
        }

        Guid organizationId;
        string? claimOrganizationId = context.User.FindFirstValue("organization_id");
        bool hasOrganizationContext = Guid.TryParse(claimOrganizationId, out organizationId)
            || workspaceCookie.TryRead(context, out organizationId);
        if (!hasOrganizationContext)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Workspace context required");
            return;
        }

        OrganizationAccess? access = await resolver.ResolveAsync(actorId, organizationId, context.RequestAborted);
        if (access is null)
        {
            workspaceCookie.Clear(context);
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Workspace access denied");
            return;
        }

        initializer.Initialize(access);
        await next(context);
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title) =>
        Results.Problem(statusCode: statusCode, title: title).ExecuteAsync(context);
}
