using System.Diagnostics.Metrics;

namespace Trykatch.Infrastructure.Caching;

internal static class OrganizationCacheTelemetry
{
    private static readonly Meter Meter = new("Trykatch.OrganizationCache");
    private static readonly Counter<long> Reads = Meter.CreateCounter<long>("trykatch.cache.reads");
    private static readonly Counter<long> Sources = Meter.CreateCounter<long>("trykatch.cache.sources");
    private static readonly Counter<long> Invalidations = Meter.CreateCounter<long>("trykatch.cache.invalidations");
    private static readonly Counter<long> Refusals = Meter.CreateCounter<long>("trykatch.cache.refusals");

    public static void Read(OrganizationCacheMode mode, bool eligible) =>
        Record(Reads, mode, eligible ? "eligible" : "bypassed");
    public static void Source(OrganizationCacheMode mode) => Record(Sources, mode, "started");
    public static void Invalidation(OrganizationCacheMode mode) => Record(Invalidations, mode, "staged");
    public static void Dirty(OrganizationCacheMode mode) => Record(Refusals, mode, "dirty");
    public static void Oversized(OrganizationCacheMode mode) => Record(Refusals, mode, "oversized");
    public static void Corrupt(OrganizationCacheMode mode) => Record(Refusals, mode, "corrupt");

    private static void Record(Counter<long> counter, OrganizationCacheMode mode, string outcome) => counter.Add(1,
        new KeyValuePair<string, object?>("mode", mode switch
        {
            OrganizationCacheMode.LocalOnly => "local",
            OrganizationCacheMode.Distributed => "distributed",
            _ => "disabled"
        }),
        new KeyValuePair<string, object?>("outcome", outcome));
}
