using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Domain;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    [TestMethod]
    public async Task CanceledOwnerWaitsForDelayedL2MissAndKeepsEfScopeAliveForSharedFactory()
    {
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        await using var valkey = await StartCacheBackendAsync();
        var settings = DistributedSettings(valkey);
        using ServiceProvider backendOwner = BuildProvider(postgres.GetConnectionString(), settings, false, new ProjectsModule());
        DelayedCacheBackend delayed = new(backendOwner.GetRequiredKeyedService<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey));
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), settings, false,
            services => services.AddKeyedSingleton<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey, delayed), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        using CancellationTokenSource ownerCancellation = new();
        int factoryCalls = 0;
        bool ownerDisposed = false;
        async Task<CacheDto> ReadAsync(CancellationToken caller, bool owner)
        {
            try
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                InitializeCacheContext(scope.ServiceProvider, organization, actor);
                await using IOrganizationDataScope data = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                    .BeginReadOnlyAsync(organization, actor, CancellationToken.None);
                return await scope.ServiceProvider.GetRequiredService<IOrganizationCache>().GetOrCreateAsync(Key("delayed-backend"), async ct =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    ownerDisposed.ShouldBeFalse("the selected owner must retain its scoped EF dependencies until source completion");
                    (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<Project>().CountAsync(ct)).ShouldBe(0);
                    return new CacheDto { Value = 7 };
                }, caller);
            }
            finally { if (owner) ownerDisposed = true; }
        }

        Task<CacheDto> canceledOwner = ReadAsync(ownerCancellation.Token, owner: true);
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<CacheDto> waiter = ReadAsync(CancellationToken.None, owner: false);
        try
        {
            ownerCancellation.Cancel();
            await Task.Delay(100);
            canceledOwner.IsCompleted.ShouldBeFalse();
            ownerDisposed.ShouldBeFalse();
            factoryCalls.ShouldBe(0);
        }
        finally { delayed.Release.TrySetResult(); }
        await Should.ThrowAsync<OperationCanceledException>(() => canceledOwner);
        (await waiter).Value.ShouldBe(7);
        ownerDisposed.ShouldBeTrue();
        factoryCalls.ShouldBe(1);
    }

    private sealed class DelayedCacheBackend(IDistributedCache inner) : IDistributedCache
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int gets;
        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            // HybridCache also reads wildcard-tag metadata during construction. Delay the
            // requested physical DTO lookup, rather than that independent startup probe.
            if (key.StartsWith("org-cache:v1:", StringComparison.Ordinal) && Interlocked.Increment(ref gets) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return await inner.GetAsync(key, token);
        }
        public byte[]? Get(string key) => inner.Get(key);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }
}
