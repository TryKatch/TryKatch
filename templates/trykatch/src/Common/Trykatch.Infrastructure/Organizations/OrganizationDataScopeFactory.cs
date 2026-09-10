using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationDataScopeFactory(OrganizationControlPlaneDbContext dbContext) : IOrganizationDataScopeFactory
{
    public async Task<IOrganizationDataScope> BeginAsync(
        Guid organizationId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("An organization data transaction is already active.");

        IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            string organization = organizationId.ToString();
            string actor = actorId.ToString();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.organization_id', {organization}, true), set_config('app.actor_id', {actor}, true)",
                cancellationToken);
            return new OrganizationDataScope(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private sealed class OrganizationDataScope(IDbContextTransaction transaction) : IOrganizationDataScope
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
