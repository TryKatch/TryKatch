using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Infrastructure.Caching;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationCacheScopeIntegrationTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task CacheMetricsUseOnlyClosedModeAndOutcomeDimensions()
    {
        ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> samples = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Trykatch.OrganizationCache") owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => samples.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.Start();
        await using PostgreSqlContainer postgres = await StartMigratedCachePostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString(), AdapterSettings(OrganizationCacheMode.LocalOnly), new ProjectsModule());
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        for (int i = 0; i < 2; i++)
            _ = await InReadOnlyScopeAsync(provider, organization, actor, cache => cache.GetOrCreateAsync(Key("metrics"),
                _ => Task.FromResult(new CacheDto { Name = "private DTO content", Value = 1 })));
        await InvalidateCacheAsync(provider, Key("metrics"), organization, actor);

        samples.Where(sample => sample.Name == "trykatch.cache.reads").Sum(sample => sample.Value).ShouldBe(2);
        samples.Where(sample => sample.Name == "trykatch.cache.sources").Sum(sample => sample.Value).ShouldBe(1);
        samples.Where(sample => sample.Name == "trykatch.cache.invalidations").Sum(sample => sample.Value).ShouldBe(1);
        foreach (var sample in samples)
        {
            sample.Tags.Select(tag => tag.Key).ShouldBe(["mode", "outcome"]);
            sample.Tags[0].Value.ShouldBe("local");
            sample.Tags[1].Value.ShouldBeOneOf("eligible", "started", "staged");
        }
    }
}
