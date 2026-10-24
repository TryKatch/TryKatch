using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    [TestMethod]
    public async Task AdapterDisabledModeCallsSourceOnEveryReadWithoutOpeningCacheTransaction()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), new ProjectsModule());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        InitializeCacheContext(scope.ServiceProvider, organizationId, actorId);
        IOrganizationCache cache = scope.ServiceProvider.GetRequiredService<IOrganizationCache>();
        int factoryCalls = 0;

        CacheDto first = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls }));
        CacheDto second = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls }));

        first.Value.ShouldBe(1);
        second.Value.ShouldBe(2);
        factoryCalls.ShouldBe(2);
        scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>().Database.CurrentTransaction.ShouldBeNull();
    }

    [TestMethod]
    public async Task AdapterLocalOnlyCachesNamedTtlAndReturnsDetachedGraphs()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        int factoryCalls = 0;

        CacheDto[] values = await InReadOnlyScopeAsync<CacheDto[]>(provider, organizationId, actorId, async cache =>
        {
            CacheDto first = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto
            {
                Value = ++factoryCalls,
                Nested = new NestedDto { Name = "detached" },
                Items = ["one", "two"]
            }));
            CacheDto second = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls }));
            return [first, second];
        });

        factoryCalls.ShouldBe(1);
        values[0].ShouldNotBeSameAs(values[1]);
        values[0].Nested.ShouldNotBeSameAs(values[1].Nested);
        values[0].Items.ShouldNotBeSameAs(values[1].Items);
        values[0].Value.ShouldBe(1);
        values[1].Value.ShouldBe(1);
        values[0].Nested!.Name = "mutated";
        values[0].Items.Add("three");
        values[1].Nested!.Name.ShouldBe("detached");
        values[1].Items.ShouldBe(["one", "two"]);
    }

    [TestMethod]
    public async Task AdapterOverlappingSameInstanceMissesShareOneFactoryAcrossScopes()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        InitializeCacheContext(firstScope.ServiceProvider, organizationId, actorId);
        InitializeCacheContext(secondScope.ServiceProvider, organizationId, actorId);
        await using IOrganizationDataScope firstData = await firstScope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        await using IOrganizationDataScope secondData = await secondScope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        TaskCompletionSource factoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CacheDto> releaseFactory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        Func<CancellationToken, Task<CacheDto>> factory = async cancellationToken =>
        {
            Interlocked.Increment(ref factoryCalls);
            factoryStarted.TrySetResult();
            return await releaseFactory.Task.WaitAsync(cancellationToken);
        };

        Task<CacheDto> first = firstScope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(Key(), factory);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<CacheDto> second = secondScope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(Key(), factory);
        await Task.Delay(100);
        factoryCalls.ShouldBe(1);
        releaseFactory.TrySetResult(new CacheDto { Value = 7, Nested = new NestedDto { Name = "shared" } });

        CacheDto[] values = await Task.WhenAll(first, second);

        factoryCalls.ShouldBe(1);
        values[0].ShouldNotBeSameAs(values[1]);
        values[0].Nested.ShouldNotBeSameAs(values[1].Nested);
        values[0].Value.ShouldBe(7);
        values[1].Value.ShouldBe(7);
    }

    [TestMethod]
    public async Task AdapterPartitionsOrganizationsAndOnlyActorAudienceByActor()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid firstOrganization = Guid.NewGuid(), secondOrganization = Guid.NewGuid();
        Guid firstActor = Guid.NewGuid(), secondActor = Guid.NewGuid();
        int factoryCalls = 0;

        async Task<string?> Read(string name, Guid organizationId, Guid actorId, ModuleCacheAudience audience)
        {
            return await InReadOnlyScopeAsync(provider, organizationId, actorId, async cache =>
            {
                CacheDto result = await cache.GetOrCreateAsync(Key("projects.detail", audience), _ => Task.FromResult(new CacheDto
                {
                    Name = name,
                    Value = Interlocked.Increment(ref factoryCalls)
                }));
                return result.Name;
            });
        }

        string? sharedFirst = await Read("first-org", firstOrganization, firstActor, ModuleCacheAudience.Organization);
        string? sharedActor = await Read("must-not-replace", firstOrganization, secondActor, ModuleCacheAudience.Organization);
        string? otherOrganization = await Read("other-org", secondOrganization, firstActor, ModuleCacheAudience.Organization);
        string? firstPrivateActor = await Read("actor-a", firstOrganization, firstActor, ModuleCacheAudience.CurrentActor);
        string? secondPrivateActor = await Read("actor-b", firstOrganization, secondActor, ModuleCacheAudience.CurrentActor);

        sharedFirst.ShouldBe("first-org");
        sharedActor.ShouldBe("first-org");
        otherOrganization.ShouldBe("other-org");
        firstPrivateActor.ShouldBe("actor-a");
        secondPrivateActor.ShouldBe("actor-b");
        factoryCalls.ShouldBe(4);
    }

    [TestMethod]
    public async Task AdapterExpiresEntriesUsingConfiguredNamedTtl()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly, shortTtlSeconds: 1), new ProjectsModule());
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        int factoryCalls = 0;

        CacheDto first = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls })));
        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        CacheDto second = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls })));

        first.Value.ShouldBe(1);
        second.Value.ShouldBe(2);
        factoryCalls.ShouldBe(2);
    }

    [TestMethod]
    public async Task AdapterNoTransactionAndReadWriteTransactionsBypassCacheAndSourceErrorsAreNotRetried()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        InitializeCacheContext(scope.ServiceProvider, organizationId, actorId);
        IOrganizationCache cache = scope.ServiceProvider.GetRequiredService<IOrganizationCache>();
        int factoryCalls = 0;

        _ = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls }));
        _ = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls }));
        await using (IOrganizationDataScope data = await BeginCacheWriteAsync(scope.ServiceProvider, organizationId, actorId))
        {
            _ = await cache.GetOrCreateAsync(Key(), _ => Task.FromResult(new CacheDto { Value = ++factoryCalls }));
            await Should.ThrowAsync<InvalidOperationException>(() => cache.GetOrCreateAsync<CacheDto>(Key(), _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                throw new InvalidOperationException("source-failure");
            }));
            factoryCalls.ShouldBe(4);
        }
    }

    [TestMethod]
    public async Task AdapterCooperativeFactoryTimeoutAndCanceledOwnerPreserveSharedWaiter()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider timeoutProvider = BuildProvider(
            postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly, factoryTimeoutSeconds: 1), new ProjectsModule());
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        int timeoutCalls = 0;
        await Should.ThrowAsync<OperationCanceledException>(() => InReadOnlyScopeAsync(timeoutProvider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(Key(), async cancellationToken =>
            {
                Interlocked.Increment(ref timeoutCalls);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new CacheDto();
            })));
        timeoutCalls.ShouldBe(1);

        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        await using AsyncServiceScope ownerScope = provider.CreateAsyncScope();
        await using AsyncServiceScope waiterScope = provider.CreateAsyncScope();
        InitializeCacheContext(ownerScope.ServiceProvider, organizationId, actorId);
        InitializeCacheContext(waiterScope.ServiceProvider, organizationId, actorId);
        await using IOrganizationDataScope ownerData = await ownerScope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        await using IOrganizationDataScope waiterData = await waiterScope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        using CancellationTokenSource ownerCancellation = new();
        TaskCompletionSource factoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CacheDto> releaseFactory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        Func<CancellationToken, Task<CacheDto>> factory = async factoryToken =>
        {
            Interlocked.Increment(ref factoryCalls);
            factoryStarted.TrySetResult();
            return await releaseFactory.Task.WaitAsync(factoryToken);
        };

        Task<CacheDto> owner = ownerScope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(Key("cancel-owner"), factory, ownerCancellation.Token);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<CacheDto> waiter = waiterScope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(Key("cancel-owner"), factory);
        await Task.Delay(100);
        ownerCancellation.Cancel();
        owner.IsCompleted.ShouldBeFalse();
        releaseFactory.TrySetResult(new CacheDto { Name = "waiter-remains" });

        await Should.ThrowAsync<OperationCanceledException>(() => owner);
        CacheDto waiterValue = await waiter;
        waiterValue.Name.ShouldBe("waiter-remains");
        factoryCalls.ShouldBe(1);
    }

    [TestMethod]
    public async Task AdapterOversizedAndCorruptCachedPayloadsFallBackOnceWithoutCachingResults()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        int largeCalls = 0;

        CacheDto large = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(Key("oversized"), _ => Task.FromResult(new CacheDto
            {
                Name = new string('x', OrganizationCacheOptions.MaximumPayloadBytes + 128),
                Value = Interlocked.Increment(ref largeCalls)
            })));
        large.Name.Length.ShouldBe(OrganizationCacheOptions.MaximumPayloadBytes + 128);
        largeCalls.ShouldBe(1);
        CacheDto next = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(Key("oversized"), _ => Task.FromResult(new CacheDto
            {
                Name = "stored-after-large",
                Value = Interlocked.Increment(ref largeCalls)
            })));
        next.Name.ShouldBe("stored-after-large");
        largeCalls.ShouldBe(2);

        using AsyncServiceScope scope = provider.CreateAsyncScope();
        InitializeCacheContext(scope.ServiceProvider, organizationId, actorId);
        await using IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        ModuleCacheKey corruptKey = Key("corrupt");
        CacheHostIdentity identity = scope.ServiceProvider.GetRequiredService<CacheHostIdentity>();
        string logicalHash = CacheKeyPolicy.LogicalHash(corruptKey, identity.ApplicationNamespace, identity.Environment, organizationId, actorId);
        string physicalKey = CacheKeyPolicy.PhysicalKey(logicalHash, Guid.Empty);
        byte[] corruptPayload = [.. CachePayloadCodec.Encode(new CacheDto())[0..33], (byte)'{'];
        HybridCache hybrid = scope.ServiceProvider.GetRequiredKeyedService<HybridCache>(OrganizationCacheRegistration.CacheServiceKey);
        await hybrid.SetAsync(physicalKey, corruptPayload, new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(1),
            LocalCacheExpiration = TimeSpan.FromMinutes(1)
        });
        int corruptCalls = 0;

        CacheDto recovered = await scope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(corruptKey, _ => Task.FromResult(new CacheDto
            {
                Name = "recovered",
                Value = ++corruptCalls
            }));

        recovered.Name.ShouldBe("recovered");
        corruptCalls.ShouldBe(1);
    }

    [TestMethod]
    public async Task AdapterRequiresInstalledModuleAndResolvedOrganizationEvenWhenDisabled()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider noModules = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope missingModuleScope = noModules.CreateAsyncScope();
        InitializeCacheContext(missingModuleScope.ServiceProvider, Guid.NewGuid(), Guid.NewGuid());
        int factoryCalls = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => missingModuleScope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(Key(), _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(new CacheDto());
            }));
        factoryCalls.ShouldBe(0);

        using ServiceProvider installed = BuildProvider(postgres.GetConnectionString(), new ProjectsModule());
        await using AsyncServiceScope unresolvedScope = installed.CreateAsyncScope();
        await Should.ThrowAsync<InvalidOperationException>(() => unresolvedScope.ServiceProvider.GetRequiredService<IOrganizationCache>()
            .GetOrCreateAsync(Key(), _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(new CacheDto());
            }));
        factoryCalls.ShouldBe(0);
    }

    [TestMethod]
    public async Task AdapterCommitInvalidatesPriorGenerationAndRollbackKeepsCachedValue()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organizationId = Guid.NewGuid(), actorId = Guid.NewGuid();
        ModuleCacheKey key = Key("invalidate");
        int factoryCalls = 0;

        CacheDto initial = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(key, _ => Task.FromResult(new CacheDto
            {
                Name = "initial",
                Value = ++factoryCalls
            })));
        initial.Name.ShouldBe("initial");

        await using (AsyncServiceScope writeScope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(writeScope.ServiceProvider, organizationId, actorId);
            await using IOrganizationDataScope data = await BeginCacheWriteAsync(writeScope.ServiceProvider, organizationId, actorId);
            await writeScope.ServiceProvider.GetRequiredService<IOrganizationCache>().InvalidateAsync(key);
            await data.CommitAsync(CancellationToken.None);
        }

        CacheDto afterCommit = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(key, _ => Task.FromResult(new CacheDto
            {
                Name = "committed",
                Value = ++factoryCalls
            })));
        afterCommit.Name.ShouldBe("committed");
        factoryCalls.ShouldBe(2);

        await using (AsyncServiceScope rollbackScope = provider.CreateAsyncScope())
        {
            InitializeCacheContext(rollbackScope.ServiceProvider, organizationId, actorId);
            await using IOrganizationDataScope data = await BeginCacheWriteAsync(rollbackScope.ServiceProvider, organizationId, actorId);
            await rollbackScope.ServiceProvider.GetRequiredService<IOrganizationCache>().InvalidateAsync(key);
        }

        CacheDto afterRollback = await InReadOnlyScopeAsync(provider, organizationId, actorId,
            cache => cache.GetOrCreateAsync(key, _ => Task.FromResult(new CacheDto { Name = "must-not-run" })));
        afterRollback.Name.ShouldBe("committed");
        factoryCalls.ShouldBe(2);
    }

    private static ModuleCacheKey Key(string readModel = "projects.detail", ModuleCacheAudience audience = ModuleCacheAudience.Organization) =>
        new("projects", readModel, 1, 1, new string('a', 64), audience, "short");

    private static Dictionary<string, string?> AdapterSettings(
        OrganizationCacheMode mode,
        int? shortTtlSeconds = null,
        int? factoryTimeoutSeconds = null)
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["Cache:Mode"] = mode.ToString()
        };
        if (shortTtlSeconds is { } shortTtl)
            settings["Cache:TtlSeconds:short"] = shortTtl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (factoryTimeoutSeconds is { } timeout)
            settings["Cache:FactoryTimeout"] = TimeSpan.FromSeconds(timeout).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
        return settings;
    }

    private static async Task<T> InReadOnlyScopeAsync<T>(
        ServiceProvider provider,
        Guid organizationId,
        Guid actorId,
        Func<IOrganizationCache, Task<T>> action)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InitializeCacheContext(scope.ServiceProvider, organizationId, actorId);
        await using IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        return await action(scope.ServiceProvider.GetRequiredService<IOrganizationCache>());
    }

    private sealed class CacheDto
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public NestedDto? Nested { get; set; }
        public List<string> Items { get; set; } = [];
    }

    private sealed class NestedDto
    {
        public string Name { get; set; } = "";
    }
}
