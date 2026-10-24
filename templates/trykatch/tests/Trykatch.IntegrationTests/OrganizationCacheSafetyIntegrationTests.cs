using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    [TestMethod]
    public async Task CacheSafetyRolledBackTrackedEntityCannotPopulateLaterReadOnlyCache()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        Guid projectId = await SeedCacheProjectAsync(provider, organization, actor);
        ModuleCacheKey key = Key("safety.rolled-back-tracking");
        await using AsyncServiceScope reused = provider.CreateAsyncScope();
        InitializeCacheContext(reused.ServiceProvider, organization, actor);
        var application = reused.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cache = reused.ServiceProvider.GetRequiredService<IOrganizationCache>();
        await using (IOrganizationDataScope write = await BeginCacheWriteAsync(reused.ServiceProvider, organization, actor))
        {
            Project project = await application.Set<Project>().SingleAsync(value => value.Id == projectId);
            project.Update("uncommitted", null);
            await application.SaveChangesAsync();
            // A miss after SaveChanges, before invalidation, must never publish uncommitted state.
            (await cache.GetOrCreateAsync(key, async token => new CacheDto
                { Name = (await application.Set<Project>().SingleAsync(value => value.Id == projectId, token)).Name })).Name.ShouldBe("uncommitted");
            await cache.InvalidateAsync(key);
        }
        application.ChangeTracker.Entries<Project>().Single().State.ShouldBe(EntityState.Unchanged);
        await using (IOrganizationDataScope read = await reused.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                         .BeginReadOnlyAsync(organization, actor, CancellationToken.None))
        {
            // Bypass does not clear this caller's legitimate EF state or pretend its identity map is fresh.
            (await cache.GetOrCreateAsync(key, async token => new CacheDto
                { Name = (await application.Set<Project>().SingleAsync(value => value.Id == projectId, token)).Name })).Name.ShouldBe("uncommitted");
        }

        CacheDto fresh = await ReadFreshCacheProjectAsync(provider, organization, actor, projectId, key);
        fresh.Name.ShouldBe("committed");
    }

    [TestMethod]
    public async Task CacheSafetyPreviouslyUnchangedTrackedEntityCannotPublishAfterAnotherWriterCommits()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        Guid projectId = await SeedCacheProjectAsync(provider, organization, actor);
        ModuleCacheKey key = Key("safety.unchanged-tracking");
        await using AsyncServiceScope reused = provider.CreateAsyncScope();
        InitializeCacheContext(reused.ServiceProvider, organization, actor);
        var application = reused.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using (IOrganizationDataScope read = await reused.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                         .BeginReadOnlyAsync(organization, actor, CancellationToken.None))
            _ = await application.Set<Project>().SingleAsync(value => value.Id == projectId);
        await using (AsyncServiceScope writer = provider.CreateAsyncScope())
        {
            InitializeCacheContext(writer.ServiceProvider, organization, actor);
            await using IOrganizationDataScope write = await BeginCacheWriteAsync(writer.ServiceProvider, organization, actor);
            var writerApplication = writer.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Project project = await writerApplication.Set<Project>().SingleAsync(value => value.Id == projectId);
            project.Update("new committed", null);
            await writerApplication.SaveChangesAsync();
            await writer.ServiceProvider.GetRequiredService<IOrganizationCache>().InvalidateAsync(key);
            await write.CommitAsync(CancellationToken.None);
        }
        await using (IOrganizationDataScope read = await reused.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                         .BeginReadOnlyAsync(organization, actor, CancellationToken.None))
        {
            var cache = reused.ServiceProvider.GetRequiredService<IOrganizationCache>();
            (await cache.GetOrCreateAsync(key, async token => new CacheDto
                { Name = (await application.Set<Project>().SingleAsync(value => value.Id == projectId, token)).Name })).Name.ShouldBe("committed");
        }

        (await ReadFreshCacheProjectAsync(provider, organization, actor, projectId, key)).Name.ShouldBe("new committed");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CacheSafetyDirtyFactoryCannotPublishWithAutomaticDetectionEnabledOrDisabled(
        bool controlPlane, bool automaticDetectionDisabled)
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        Guid projectId = await SeedCacheProjectAsync(provider, organization, actor);
        ModuleCacheKey key = Key("safety.dirty-factory");
        int sourceCalls = 0;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(scope.ServiceProvider, organization, actor);
            await using IOrganizationDataScope read = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginReadOnlyAsync(organization, actor, CancellationToken.None);
            var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
            application.ChangeTracker.AutoDetectChangesEnabled = !automaticDetectionDisabled;
            control.ChangeTracker.AutoDetectChangesEnabled = !automaticDetectionDisabled;
            CacheDto value = await scope.ServiceProvider.GetRequiredService<IOrganizationCache>().GetOrCreateAsync(key, async token =>
            {
                sourceCalls++;
                if (controlPlane)
                {
                    control.Roles.Add(Role.Create(organization, "uncommitted"));
                    return new CacheDto { Name = "uncommitted" };
                }
                Project project = await application.Set<Project>().SingleAsync(value => value.Id == projectId, token);
                project.Update("uncommitted", null);
                return new CacheDto { Name = project.Name };
            });
            value.Name.ShouldBe("uncommitted");
            sourceCalls.ShouldBe(1);
            (controlPlane ? control.ChangeTracker : application.ChangeTracker).Entries().ShouldNotBeEmpty();
        }

        (await ReadFreshCacheProjectAsync(provider, organization, actor, projectId, key)).Name.ShouldBe("committed");
    }

    [TestMethod]
    public async Task CacheSafetyFreshTrackingFactoryCanCacheCopiedDtoAndSourceFailuresAreNotRetried()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        Guid projectId = await SeedCacheProjectAsync(provider, organization, actor);
        int sourceCalls = 0;
        ModuleCacheKey key = Key("safety.fresh-tracking");
        await ReadFreshCacheProjectAsync(provider, organization, actor, projectId, key, () => sourceCalls++);
        await ReadFreshCacheProjectAsync(provider, organization, actor, projectId, key, () => sourceCalls++);
        sourceCalls.ShouldBe(1);
        int failures = 0;
        await Should.ThrowAsync<CacheSourceException>(() => InReadOnlyScopeAsync(provider, organization, actor, cache =>
            cache.GetOrCreateAsync<CacheDto>(Key("safety.source-error"), _ =>
            {
                failures++;
                throw new CacheSourceException();
            })));
        failures.ShouldBe(1);
    }

    [TestMethod]
    public async Task CacheSafetyMappedEntityBehindObjectPropertyIsRejectedWithoutRetry()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        int calls = 0;
        NotSupportedException failure = await Should.ThrowAsync<NotSupportedException>(() => InReadOnlyScopeAsync(
            provider, Guid.NewGuid(), Guid.NewGuid(), cache => cache.GetOrCreateAsync(Key("safety.mapped-entity"), _ =>
            {
                calls++;
                return Task.FromResult(new CacheEntityContainer(new OutboxMessage { Payload = "private mapped payload" }));
            })));
        calls.ShouldBe(1);
        failure.Message.ShouldNotContain("private mapped payload");
    }

    private static async Task<Guid> SeedCacheProjectAsync(ServiceProvider provider, Guid organization, Guid actor)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InitializeCacheContext(scope.ServiceProvider, organization, actor);
        await using IOrganizationDataScope write = await BeginCacheWriteAsync(scope.ServiceProvider, organization, actor);
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Project project = Project.Create(organization, "committed", null, actor);
        application.Set<Project>().Add(project);
        await application.SaveChangesAsync();
        await write.CommitAsync(CancellationToken.None);
        return project.Id;
    }

    private static async Task<CacheDto> ReadFreshCacheProjectAsync(
        ServiceProvider provider, Guid organization, Guid actor, Guid projectId, ModuleCacheKey key, Action? onSource = null)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InitializeCacheContext(scope.ServiceProvider, organization, actor);
        await using IOrganizationDataScope read = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organization, actor, CancellationToken.None);
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IOrganizationCache>().GetOrCreateAsync(key, async token =>
        {
            onSource?.Invoke();
            Project project = await application.Set<Project>().SingleAsync(value => value.Id == projectId, token);
            return new CacheDto { Name = project.Name };
        });
    }

    private sealed record CacheEntityContainer(object Value);
    private sealed class CacheSourceException : Exception;
}
