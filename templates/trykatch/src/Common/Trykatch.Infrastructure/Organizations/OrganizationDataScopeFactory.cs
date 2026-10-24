using System.Data;
using Trykatch.Infrastructure.Caching;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationDataScopeFactory(
    OrganizationControlPlaneDbContext dbContext,
    ApplicationDbContext applicationDbContext,
    CacheInvalidationCommitState cacheInvalidations) : IOrganizationDataScopeFactory
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
            return new OrganizationDataScope(transaction, applicationDbContext, cacheInvalidations);
        }
        catch
        {
            try
            {
                await DisposeEnlistedApplicationTransactionAsync(applicationDbContext, transaction);
            }
            finally
            {
                await transaction.DisposeAsync();
            }
            throw;
        }
    }

    public async Task<IOrganizationDataScope> BeginReadOnlyAsync(
        Guid organizationId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty || actorId == Guid.Empty)
            throw new ArgumentException("A nonempty organization and actor are required for a read-only scope.");
        if (dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("An organization data transaction is already active.");

        IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            string organization = organizationId.ToString();
            string actor = actorId.ToString();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.organization_id', {organization}, true), set_config('app.actor_id', {actor}, true)",
                cancellationToken);
            _ = await OrganizationTransactionEnlistment.EnlistAsync(
                dbContext, applicationDbContext, organizationId, actorId, cancellationToken);
            return new OrganizationDataScope(transaction, applicationDbContext, cacheInvalidations);
        }
        catch
        {
            try
            {
                await DisposeEnlistedApplicationTransactionAsync(applicationDbContext, transaction);
            }
            finally
            {
                await transaction.DisposeAsync();
            }
            throw;
        }
    }

    private static async ValueTask DisposeEnlistedApplicationTransactionAsync(
        ApplicationDbContext applicationDbContext,
        IDbContextTransaction ownerTransaction)
    {
        IDbContextTransaction? applicationTransaction = applicationDbContext.Database.CurrentTransaction;
        if (applicationTransaction is not null
            && ReferenceEquals(applicationTransaction.GetDbTransaction(), ownerTransaction.GetDbTransaction()))
        {
            await applicationTransaction.DisposeAsync();
        }
    }

    private sealed class OrganizationDataScope(
        IDbContextTransaction transaction,
        ApplicationDbContext applicationDbContext,
        CacheInvalidationCommitState cacheInvalidations) : IOrganizationDataScope
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            cacheInvalidations.Discard(transaction.GetDbTransaction());
            try
            {
                await DisposeEnlistedApplicationTransactionAsync(applicationDbContext, transaction);
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
