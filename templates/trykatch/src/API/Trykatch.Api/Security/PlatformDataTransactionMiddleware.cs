using System.Security.Claims;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Trykatch.Api.Security;

public sealed class AtomicMutationResponseOptions
{
    public const string SectionName = "AtomicMutationResponse";
    public int MaximumBytes { get; init; } = 1_048_576;
}

public sealed class PlatformDataTransactionMiddleware(
    RequestDelegate next,
    IOptions<AtomicMutationResponseOptions> responseOptions)
{
    public async Task InvokeAsync(
        HttpContext context,
        OrganizationControlPlaneDbContext organizationDbContext,
        IWorkspaceContextCookie workspaceCookie)
    {
        Endpoint? endpoint = context.GetEndpoint();
        bool usesPlatformData = endpoint?.Metadata.GetMetadata<PlatformDataScopedAttribute>() is not null
            || endpoint?.Metadata.GetMetadata<IOrganizationScopedMetadata>() is not null;
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

        bool platformWorkflow = endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy?.StartsWith("platform-permission:", StringComparison.Ordinal) == true);
        if (platformWorkflow && endpoint.Metadata.GetMetadata<IOrganizationScopedMetadata>() is not null)
            throw new InvalidOperationException("An organization endpoint cannot request platform database privileges.");
        DbContext platformDbContext = platformWorkflow
            ? context.RequestServices.GetRequiredService<PlatformDbContext>()
            : organizationDbContext;
        await using IDbContextTransaction transaction = await platformDbContext.Database.BeginTransactionAsync(context.RequestAborted);
        string actor = actorId.ToString();
        Guid organizationId;
        string? organizationClaim = context.User.FindFirstValue("organization_id");
        bool hasOrganization = Guid.TryParse(organizationClaim, out organizationId)
            || workspaceCookie.TryRead(context, out organizationId);
        string organization = !platformWorkflow && hasOrganization ? organizationId.ToString() : string.Empty;
        await platformDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.actor_id', {actor}, true), set_config('app.organization_id', {organization}, true)",
            context.RequestAborted);

        bool mutation = HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);
        if (!mutation)
        {
            await next(context);
            if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
                await transaction.CommitAsync(context.RequestAborted);
            return;
        }

        await AtomicMutationResponse.ExecuteAsync(
            context,
            responseOptions.Value.MaximumBytes,
            next,
            async cancellationToken =>
            {
                if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
                    await transaction.CommitAsync(cancellationToken);
                else
                    await transaction.RollbackAsync(CancellationToken.None);
            });
    }
}
