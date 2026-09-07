using FlatpackApp.Application.Organizations;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlatpackApp.Api.Security;

public sealed class OrganizationTransactionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IOrganizationContext organization,
        ApplicationDbContext applicationDbContext,
        PlatformDbContext platformDbContext)
    {
        if (!organization.IsResolved)
        {
            await next(context);
            return;
        }

        await using IDbContextTransaction applicationTransaction = await applicationDbContext.Database.BeginTransactionAsync(context.RequestAborted);
        await using IDbContextTransaction platformTransaction = await platformDbContext.Database.BeginTransactionAsync(context.RequestAborted);
        string organizationId = organization.OrganizationId.ToString();
        string actorId = organization.ActorId.ToString();
        await applicationDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationId}, true), set_config('app.actor_id', {actorId}, true)",
            context.RequestAborted);
        await platformDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationId}, true), set_config('app.actor_id', {actorId}, true)",
            context.RequestAborted);
        await next(context);
        if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
        {
            await applicationTransaction.CommitAsync(context.RequestAborted);
            await platformTransaction.CommitAsync(context.RequestAborted);
        }
    }
}
