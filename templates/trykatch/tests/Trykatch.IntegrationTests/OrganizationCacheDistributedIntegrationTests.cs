using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Caching;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    private const string CacheImage = "valkey/valkey:9.1.2-alpine3.24@sha256:48332870af354a799964c0012ae1194a0bf2bf894eb508f945810596dc2d8d11";
    private const string CachePassword = "acceptance-only-cache-password";

    [TestMethod]
    public async Task DistributedCacheUsesAuthenticatedL2AndPartitionsOrganizationsAndActors()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        await using IContainer valkey = await StartCacheBackendAsync();
        var settings = DistributedSettings(valkey);
        using ServiceProvider first = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        first.GetRequiredKeyedService<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey).ShouldBeOfType<RedisCache>();
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid(), otherActor = Guid.NewGuid(), otherOrganization = Guid.NewGuid();
        int factories = 0;
        async Task<CacheDto> Read(ServiceProvider provider, Guid org, Guid who, ModuleCacheAudience audience) =>
            await InReadOnlyScopeAsync(provider, org, who, cache => cache.GetOrCreateAsync(Key("distributed", audience),
                _ => Task.FromResult(new CacheDto { Value = Interlocked.Increment(ref factories) })));

        (await Read(first, organization, actor, ModuleCacheAudience.Organization)).Value.ShouldBe(1);
        await AwaitL2Async(first, Key("distributed"), organization, actor);
        using ServiceProvider peer = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        (await Read(peer, organization, otherActor, ModuleCacheAudience.Organization)).Value.ShouldBe(1);
        factories.ShouldBe(1, "a fresh provider must read the actual L2 payload, without calling the source");
        (await Read(peer, otherOrganization, actor, ModuleCacheAudience.Organization)).Value.ShouldBe(2);
        (await Read(first, organization, actor, ModuleCacheAudience.CurrentActor)).Value.ShouldBe(3);
        await AwaitL2Async(first, Key("distributed", ModuleCacheAudience.CurrentActor), organization, actor);
        (await Read(peer, organization, otherActor, ModuleCacheAudience.CurrentActor)).Value.ShouldBe(4);
    }

    [TestMethod]
    public async Task DistributedGenerationRejectsIdlePeerAndLateFactoryAndWritesWhileDisabled()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        await using IContainer valkey = await StartCacheBackendAsync();
        var settings = DistributedSettings(valkey);
        using ServiceProvider first = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        using ServiceProvider idle = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        ModuleCacheKey key = Key("replica-generation");
        Task<CacheDto> Read(ServiceProvider provider, int value) => InReadOnlyScopeAsync(provider, organization, actor,
            cache => cache.GetOrCreateAsync(key, _ => Task.FromResult(new CacheDto { Value = value })));
        (await Read(first, 1)).Value.ShouldBe(1);
        await AwaitL2Async(first, key, organization, actor);
        (await Read(idle, 99)).Value.ShouldBe(1);

        // Disabled writers deliberately leave old L1/L2 entries intact. Only durable generations
        // can keep a previously idle peer from selecting those old entries when it returns.
        using ServiceProvider disabled = BuildProvider(postgres.GetConnectionString(), new Dictionary<string, string?>(), new ProjectsModule());
        await InvalidateCacheAsync(disabled, key, organization, actor);
        (await Read(idle, 2)).Value.ShouldBe(2);
        await AwaitL2Async(idle, key, organization, actor);
        using ServiceProvider restarted = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        (await Read(restarted, 99)).Value.ShouldBe(2);

        ModuleCacheKey lateKey = Key("late-factory");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CacheDto> late = InReadOnlyScopeAsync(first, organization, actor, cache => cache.GetOrCreateAsync(lateKey, async ct =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return new CacheDto { Value = 10 };
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await InvalidateCacheAsync(disabled, lateKey, organization, actor);
        release.SetResult();
        (await late).Value.ShouldBe(10);
        // Wait for publication to the obsolete key before checking the current generation.
        await AwaitL2Async(first, lateKey, organization, actor, Guid.Empty);
        (await InReadOnlyScopeAsync(restarted, organization, actor, cache => cache.GetOrCreateAsync(lateKey,
            _ => Task.FromResult(new CacheDto { Value = 20 })))).Value.ShouldBe(20);
    }

    [TestMethod]
    public async Task DistributedOutageCallsSourceOnceAndSameClientRecoversAfterFixedEndpointRestart()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        await using IContainer valkey = await StartCacheBackendAsync();
        var settings = DistributedSettings(valkey);
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        ModuleCacheKey before = Key("before-outage");
        _ = await InReadOnlyScopeAsync(provider, organization, actor, cache => cache.GetOrCreateAsync(before,
            _ => Task.FromResult(new CacheDto { Value = 1 })));
        await AwaitL2Async(provider, before, organization, actor);
        (await CacheHealthAsync(provider)).Status.ShouldBe(HealthStatus.Healthy);
        await valkey.StopAsync();
        HealthReportEntry unavailable = await CacheHealthAsync(provider);
        unavailable.Status.ShouldBe(HealthStatus.Degraded);
        unavailable.Exception.ShouldBeNull();
        unavailable.Data.ShouldBeEmpty();
        int calls = 0;
        (await InReadOnlyScopeAsync(provider, organization, actor, cache => cache.GetOrCreateAsync(Key("during-outage"),
            _ => Task.FromResult(new CacheDto { Value = ++calls })))).Value.ShouldBe(1);
        calls.ShouldBe(1);
        InvalidOperationException sourceFailure = new("source failure");
        var actual = await Should.ThrowAsync<InvalidOperationException>(() => InReadOnlyScopeAsync(provider, organization, actor,
            cache => cache.GetOrCreateAsync<CacheDto>(Key("failing-source"), _ => { calls++; throw sourceFailure; })));
        actual.ShouldBeSameAs(sourceFailure);
        calls.ShouldBe(2);
        await valkey.StartAsync();
        IDistributedCache backend = provider.GetRequiredKeyedService<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey);
        await EventuallyAsync(async () =>
        {
            await backend.SetAsync("recovery-probe", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10) });
            return (await backend.GetAsync("recovery-probe"))?.SequenceEqual(new byte[] { 1 }) == true;
        });
        (await CacheHealthAsync(provider)).Status.ShouldBe(HealthStatus.Healthy);
        ModuleCacheKey recovered = Key("after-outage");
        (await InReadOnlyScopeAsync(provider, organization, actor, cache => cache.GetOrCreateAsync(recovered,
            _ => Task.FromResult(new CacheDto { Value = 42 })))).Value.ShouldBe(42);
        await AwaitL2Async(provider, recovered, organization, actor);
        using ServiceProvider peer = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        (await InReadOnlyScopeAsync(peer, organization, actor, cache => cache.GetOrCreateAsync<CacheDto>(recovered,
            _ => throw new InvalidOperationException("Recovered L2 was not used.")))).Value.ShouldBe(42);
    }

    internal static async Task<IContainer> StartCacheBackendAsync()
    {
        // Docker reassigns random bindings on restart; reserve a stable loopback endpoint for
        // this fixture so recovery exercises the same already-created client.
        using TcpListener portReservation = new(IPAddress.Loopback, 0);
        portReservation.Start();
        int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        IContainer container = new ContainerBuilder(CacheImage)
            .WithCommand("valkey-server", "--requirepass", CachePassword, "--save", "", "--appendonly", "no")
            .WithPortBinding(port, 6379)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        try { await container.StartAsync(); return container; }
        catch { await container.DisposeAsync(); throw; }
    }

    internal static Dictionary<string, string?> DistributedSettings(IContainer valkey) => new()
    {
        ["Cache:Mode"] = "Distributed",
        ["Cache:TtlSeconds:short"] = "120",
        ["ConnectionStrings:cache"] = $"{valkey.Hostname}:{valkey.GetMappedPublicPort(6379)},password={CachePassword}"
    };

    private static async Task InvalidateCacheAsync(ServiceProvider provider, ModuleCacheKey key, Guid organization, Guid actor)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InitializeCacheContext(scope.ServiceProvider, organization, actor);
        await using IOrganizationDataScope transaction = await BeginCacheWriteAsync(scope.ServiceProvider, organization, actor);
        await scope.ServiceProvider.GetRequiredService<IOrganizationCache>().InvalidateAsync(key);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task AwaitL2Async(ServiceProvider provider, ModuleCacheKey key, Guid organization, Guid actor, Guid? generation = null)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InitializeCacheContext(scope.ServiceProvider, organization, actor);
        await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organization, actor, CancellationToken.None);
        CacheHostIdentity identity = provider.GetRequiredService<CacheHostIdentity>();
        string hash = CacheKeyPolicy.LogicalHash(key, identity.ApplicationNamespace, identity.Environment, organization, actor);
        Guid current = generation ?? (await scope.ServiceProvider.GetRequiredService<CacheGenerationStore>().ReadAsync(hash)).Generation;
        string physical = CacheKeyPolicy.PhysicalKey(hash, current);
        IDistributedCache backend = provider.GetRequiredKeyedService<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey);
        await EventuallyAsync(async () => (await backend.GetAsync(physical)) is not null);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (true)
        {
            try { if (await condition()) return; }
            catch (StackExchange.Redis.RedisException) { }
            await Task.Delay(100, deadline.Token);
        }
    }

    private static async Task<HealthReportEntry> CacheHealthAsync(ServiceProvider provider) =>
        (await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Name == "organization-cache")).Entries["organization-cache"];
}
