using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Trykatch.Infrastructure.Persistence;

/// <summary>Enlists application data in the active same-role control-plane transaction.</summary>
public static class OrganizationTransactionEnlistment
{
    public static async Task<IDbContextTransaction> EnlistAsync(
        OrganizationControlPlaneDbContext controlPlane,
        ApplicationDbContext application,
        Guid organizationId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction controlTransaction = controlPlane.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Organization data requires an actor-scoped control-plane transaction.");
        if (application.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Application data is already enlisted in a transaction.");

        application.Database.SetDbConnection(controlPlane.Database.GetDbConnection(), contextOwnsConnection: false);
        IDbContextTransaction applicationTransaction = await application.Database.UseTransactionAsync(
                controlTransaction.GetDbTransaction(), cancellationToken)
            ?? throw new InvalidOperationException("Application data could not enlist in the organization transaction.");
        string organization = organizationId.ToString();
        string actor = actorId.ToString();
        await application.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organization}, true), set_config('app.actor_id', {actor}, true)",
            cancellationToken);
        return applicationTransaction;
    }
}
