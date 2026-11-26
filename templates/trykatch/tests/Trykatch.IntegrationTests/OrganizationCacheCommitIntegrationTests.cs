using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    [TestMethod]
    public async Task OwnerCommitEvictsOnceAndApplicationEnlistmentPreservesPendingWork()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<CacheInvalidationCommitState>();
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        await using IOrganizationDataScope dataScope = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(organizationId, actorId, CancellationToken.None);
        DbTransaction transaction = control.Database.CurrentTransaction!.GetDbTransaction();
        int removals = 0;
        state.Register(transaction, "old-generation", _ => { removals++; return ValueTask.CompletedTask; });
        await OrganizationTransactionEnlistment.EnlistAsync(control, application, organizationId, actorId, CancellationToken.None);
        removals.ShouldBe(0);

        await dataScope.CommitAsync(CancellationToken.None);

        removals.ShouldBe(1);
        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
        removals.ShouldBe(1);
    }

    [TestMethod]
    public async Task SynchronousApplicationCommitAlsoDrainsSharedTransactionState()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<CacheInvalidationCommitState>();
        await using IOrganizationDataScope dataScope = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>().BeginReadOnlyAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        DbTransaction transaction = application.Database.CurrentTransaction!.GetDbTransaction();
        int removals = 0;
        state.Register(transaction, "old-generation", _ => { removals++; return ValueTask.CompletedTask; });

        application.Database.CurrentTransaction!.Commit();

        removals.ShouldBe(1);
        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
    }

    [TestMethod]
    public async Task ImplicitRollbackThenCommitInSameScopeNeverEvictsRolledBackKey()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<CacheInvalidationCommitState>();
        var factory = scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>();
        int rolledBackRemovals = 0, committedRemovals = 0;
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        DbTransaction rolledBack;
        await using (IOrganizationDataScope first = await factory.BeginAsync(organizationId, actorId, CancellationToken.None))
        {
            await OrganizationTransactionEnlistment.EnlistAsync(control, application, organizationId, actorId, CancellationToken.None);
            rolledBack = control.Database.CurrentTransaction!.GetDbTransaction();
            state.Register(rolledBack, "rolled-back-generation", _ => { rolledBackRemovals++; return ValueTask.CompletedTask; });
        }
        await using (IOrganizationDataScope second = await factory.BeginAsync(organizationId, actorId, CancellationToken.None))
        {
            var committed = control.Database.CurrentTransaction!.GetDbTransaction();
            state.Register(committed, "committed-generation", _ => { committedRemovals++; return ValueTask.CompletedTask; });
            await second.CommitAsync(CancellationToken.None);
        }

        rolledBackRemovals.ShouldBe(0);
        committedRemovals.ShouldBe(1);
        (await state.FlushAfterCommitAsync(rolledBack)).ShouldBe(0);
    }

    [TestMethod]
    public async Task ExplicitRollbackDiscardsPendingEviction()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<CacheInvalidationCommitState>();
        await using IOrganizationDataScope dataScope = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var transaction = control.Database.CurrentTransaction!.GetDbTransaction();
        int removals = 0;
        state.Register(transaction, "rolled-back-generation", _ => { removals++; return ValueTask.CompletedTask; });

        await control.Database.CurrentTransaction!.RollbackAsync();

        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
        removals.ShouldBe(0);
    }

    [TestMethod]
    public async Task CacheAdapterFailureAfterCommitDoesNotFailScopeCommit()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<CacheInvalidationCommitState>();
        await using IOrganizationDataScope dataScope = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var transaction = control.Database.CurrentTransaction!.GetDbTransaction();
        int removals = 0;
        state.Register(transaction, "unavailable", _ => { removals++; throw new InvalidOperationException("private adapter details"); });

        await dataScope.CommitAsync(CancellationToken.None);

        removals.ShouldBe(1);
        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
    }
}
