using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    private const string CacheHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [TestMethod]
    public async Task ConcurrentFirstInvalidationsAndRepeatedReplacementCaptureExactPriorGeneration()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider firstProvider = BuildProvider(postgres.GetConnectionString());
        string secondConnection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
            { ApplicationName = "cache-gen-second" }.ConnectionString;
        using ServiceProvider secondProvider = BuildProvider(secondConnection);
        await using AsyncServiceScope first = firstProvider.CreateAsyncScope();
        await using AsyncServiceScope second = secondProvider.CreateAsyncScope();
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        InitializeCacheContext(first.ServiceProvider, organization, actor);
        InitializeCacheContext(second.ServiceProvider, organization, actor);
        await using IOrganizationDataScope firstData = await BeginCacheWriteAsync(first.ServiceProvider, organization, actor);
        await using IOrganizationDataScope secondData = await BeginCacheWriteAsync(second.ServiceProvider, organization, actor);
        var firstStore = first.ServiceProvider.GetRequiredService<CacheGenerationStore>();
        var secondStore = second.ServiceProvider.GetRequiredService<CacheGenerationStore>();
        (await firstStore.InvalidateAsync(CacheHash)).ShouldBe(Guid.Empty);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Task<Guid> secondInvalidation = secondStore.InvalidateAsync(CacheHash, deadline.Token);
        await using NpgsqlConnection owner = new(postgres.GetConnectionString());
        await owner.OpenAsync(deadline.Token);
        bool waiting = false;
        while (!waiting && !secondInvalidation.IsCompleted)
        {
            await using NpgsqlCommand query = new("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE application_name = 'cache-gen-second' AND wait_event = 'advisory')
                """, owner);
            waiting = (bool)(await query.ExecuteScalarAsync(deadline.Token))!;
            if (!waiting) await Task.Delay(10, deadline.Token);
        }
        waiting.ShouldBeTrue("The competing writer must wait on the database transaction lock.");
        await firstData.CommitAsync(CancellationToken.None);
        Guid firstGeneration = await ReadOwnerGenerationAsync(postgres.GetConnectionString(), organization);
        (await secondInvalidation).ShouldBe(firstGeneration);
        Guid replacedInsideSecond = await secondStore.InvalidateAsync(CacheHash);
        replacedInsideSecond.ShouldNotBe(Guid.Empty);
        replacedInsideSecond.ShouldNotBe(firstGeneration);
        await secondData.CommitAsync(CancellationToken.None);
        Guid final = await ReadOwnerGenerationAsync(postgres.GetConnectionString(), organization);
        final.ShouldNotBe(firstGeneration);
        final.ShouldNotBe(replacedInsideSecond);
    }

    [TestMethod]
    public async Task GenerationAndBusinessChangesCommitOrRollBackTogether()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        var committedProject = Project.Create(organization, "Committed cache generation", null, actor);
        var rolledBackProject = Project.Create(organization, "Rolled back cache generation", null, actor);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(scope.ServiceProvider, organization, actor);
            await using IOrganizationDataScope data = await BeginCacheWriteAsync(scope.ServiceProvider, organization, actor);
            var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            application.Set<Project>().Add(committedProject);
            await application.SaveChangesAsync();
            (await scope.ServiceProvider.GetRequiredService<CacheGenerationStore>().InvalidateAsync(CacheHash)).ShouldBe(Guid.Empty);
            await data.CommitAsync(CancellationToken.None);
        }
        Guid committed = await ReadOwnerGenerationAsync(postgres.GetConnectionString(), organization);
        committed.ShouldNotBe(Guid.Empty);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(scope.ServiceProvider, organization, actor);
            await using IOrganizationDataScope data = await BeginCacheWriteAsync(scope.ServiceProvider, organization, actor);
            var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            application.Set<Project>().Add(rolledBackProject);
            await application.SaveChangesAsync();
            (await scope.ServiceProvider.GetRequiredService<CacheGenerationStore>().InvalidateAsync(CacheHash)).ShouldBe(committed);
        }

        (await ReadOwnerGenerationAsync(postgres.GetConnectionString(), organization)).ShouldBe(committed);
        await using NpgsqlConnection owner = new(postgres.GetConnectionString());
        await owner.OpenAsync();
        await using NpgsqlCommand query = new("SELECT \"Id\" FROM app.projects WHERE \"OrganizationId\" = @organization", owner);
        query.Parameters.AddWithValue("organization", organization);
        List<Guid> projects = [];
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) projects.Add(reader.GetGuid(0));
        projects.ShouldBe([committedProject.Id]);
    }

    [TestMethod]
    public async Task ReadAdmissionRequiresRealReadOnlyReadCommittedTransaction()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        InitializeCacheContext(scope.ServiceProvider, organization, actor);
        var store = scope.ServiceProvider.GetRequiredService<CacheGenerationStore>();
        (await store.ReadAsync(CacheHash)).CanCache.ShouldBeFalse();
        await using (IOrganizationDataScope data = await BeginCacheWriteAsync(scope.ServiceProvider, organization, actor))
            (await store.ReadAsync(CacheHash)).CanCache.ShouldBeFalse();
        await using (IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                         .BeginReadOnlyAsync(organization, actor, CancellationToken.None))
        {
            CacheReadGeneration generation = await store.ReadAsync(CacheHash);
            generation.CanCache.ShouldBeTrue();
            generation.Generation.ShouldBe(Guid.Empty);
        }
        var control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using IDbContextTransaction repeatable = await control.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await control.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        await using IDbContextTransaction enlisted = await OrganizationTransactionEnlistment.EnlistAsync(
            control, application, organization, actor, CancellationToken.None);
        (await store.ReadAsync(CacheHash)).CanCache.ShouldBeFalse();
    }

    [TestMethod]
    public async Task DifferentOrganizationsHaveIndependentCommittedGenerations()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        Guid actor = Guid.NewGuid(), first = Guid.NewGuid(), other = Guid.NewGuid();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(scope.ServiceProvider, first, actor);
            await using IOrganizationDataScope data = await BeginCacheWriteAsync(scope.ServiceProvider, first, actor);
            await scope.ServiceProvider.GetRequiredService<CacheGenerationStore>().InvalidateAsync(CacheHash);
            await data.CommitAsync(CancellationToken.None);
        }
        Guid expected = await ReadOwnerGenerationAsync(postgres.GetConnectionString(), first);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(scope.ServiceProvider, other, actor);
            await using IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginReadOnlyAsync(other, actor, CancellationToken.None);
            (await scope.ServiceProvider.GetRequiredService<CacheGenerationStore>().ReadAsync(CacheHash)).Generation.ShouldBe(Guid.Empty);
        }
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(scope.ServiceProvider, first, actor);
            await using IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginReadOnlyAsync(first, actor, CancellationToken.None);
            (await scope.ServiceProvider.GetRequiredService<CacheGenerationStore>().ReadAsync(CacheHash)).Generation.ShouldBe(expected);
        }
    }

    [TestMethod]
    public async Task GenerationStoreRejectsUnresolvedOrMismatchedActorScope()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<CacheGenerationStore>();
        await Should.ThrowAsync<InvalidOperationException>(() => store.ReadAsync(CacheHash));
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        InitializeCacheContext(scope.ServiceProvider, organization, actor);
        await using IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organization, Guid.NewGuid(), CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(() => store.ReadAsync(CacheHash));
    }

    private static async Task<PostgreSqlContainer> StartMigratedCachePostgresAsync()
    {
        PostgreSqlContainer postgres = await StartPostgresAsync();
        try
        {
            var migrated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(postgres.GetConnectionString());
            migrated.Exit.ShouldBe(0, migrated.Error + migrated.Output);
            return postgres;
        }
        catch { await postgres.DisposeAsync(); throw; }
    }

    private static void InitializeCacheContext(IServiceProvider services, Guid organization, Guid actor) =>
        services.GetRequiredService<IOrganizationContextInitializer>().Initialize(
            new OrganizationAccess(organization, "cache-test", actor, Guid.NewGuid(), new HashSet<string>(StringComparer.Ordinal)));

    private static async Task<IOrganizationDataScope> BeginCacheWriteAsync(IServiceProvider services, Guid organization, Guid actor)
    {
        IOrganizationDataScope data = await services.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(organization, actor, CancellationToken.None);
        try
        {
            await OrganizationTransactionEnlistment.EnlistAsync(
                services.GetRequiredService<OrganizationControlPlaneDbContext>(), services.GetRequiredService<ApplicationDbContext>(),
                organization, actor, CancellationToken.None);
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }

    private static async Task<Guid> ReadOwnerGenerationAsync(string ownerConnection, Guid organization)
    {
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"Generation\" FROM app.cache_invalidations WHERE \"OrganizationId\" = @organization AND \"KeyHash\" = @hash", owner);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("hash", CacheHash);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }
}
