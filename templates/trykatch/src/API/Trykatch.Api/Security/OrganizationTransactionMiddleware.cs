using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Trykatch.Api.Security;

public sealed class OrganizationTransactionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IOrganizationContext organization,
        ApplicationDbContext applicationDbContext,
        OrganizationControlPlaneDbContext platformDbContext)
    {
        if (!organization.IsResolved)
        {
            await next(context);
            return;
        }

        if (platformDbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Organization data requires an actor-scoped platform transaction.");

        await using IDbContextTransaction applicationTransaction = await OrganizationTransactionEnlistment.EnlistAsync(
            platformDbContext,
            applicationDbContext,
            organization.OrganizationId,
            organization.ActorId,
            context.RequestAborted);
        string organizationId = organization.OrganizationId.ToString();
        string actorId = organization.ActorId.ToString();
        await platformDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationId}, true), set_config('app.actor_id', {actorId}, true)",
            context.RequestAborted);
        await next(context);
    }
}
