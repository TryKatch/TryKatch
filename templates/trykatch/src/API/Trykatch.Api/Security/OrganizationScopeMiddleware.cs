using System.Security.Claims;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.Api.Security;

public sealed class OrganizationScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IWorkspaceContextCookie workspaceCookie,
        IOrganizationAccessResolver resolver,
        IOrganizationContextInitializer initializer,
        IOrganizationDataPlacement dataPlacement)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IOrganizationScopedMetadata>() is null)
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

        OrganizationDataRoute route;
        try
        {
            route = await dataPlacement.ResolveAsync(organizationId, context.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            OrganizationDataPlacementResult provisioned = await dataPlacement.ProvisionAsync(
                new(organizationId, OrganizationDataPlacementKind.Shared),
                context.RequestAborted);
            if (!provisioned.IsReady)
            {
                await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "Workspace data placement is not ready");
                return;
            }
            route = provisioned.Route!;
        }
        catch (InvalidOperationException)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "Workspace data placement is not ready");
            return;
        }

        if (route.Placement != OrganizationDataPlacementKind.Shared)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "unsupported_tenant_placement",
                "This workspace uses dedicated database placement, which this host cannot route. No shared database fallback was attempted.");
            return;
        }

        initializer.Initialize(access);
        await next(context);
    }

    private static Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string? detail = null) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail).ExecuteAsync(context);
}
