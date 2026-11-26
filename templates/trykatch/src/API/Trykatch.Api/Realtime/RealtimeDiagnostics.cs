using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Diagnostics.Metrics;

namespace Trykatch.Api.Realtime;

internal enum RealtimeOutcome { Admitted, Rejected, Sent, Suppressed, Closed, Failed }

internal static class RealtimeMetrics
{
    private static readonly Meter Meter = new("Trykatch.Realtime");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("trykatch.realtime.outcomes");

    internal static void Record(RealtimeMode mode, RealtimeOutcome outcome)
    {
        string modeName = mode switch
        {
            RealtimeMode.Disabled => "disabled", RealtimeMode.LocalOnly => "local", RealtimeMode.Distributed => "distributed",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        Outcomes.Add(1, new("mode", modeName), new("outcome", outcome.ToString().ToLowerInvariant()));
    }
}

internal sealed class RealtimeBackplaneProbe(ConfigurationOptions settings) : IAsyncDisposable
{
    // A separate lazy probe owns its connection; the standard backplane retains its own framework-managed connection.
    private readonly Lazy<Task<ConnectionMultiplexer>> connection = new(() => ConnectionMultiplexer.ConnectAsync(settings));

    internal async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionMultiplexer current = await connection.Value;
        cancellationToken.ThrowIfCancellationRequested();
        if (!current.IsConnected) return false;
        _ = await current.GetDatabase().PingAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!connection.IsValueCreated) return;
        try { (await connection.Value).Dispose(); }
        catch (Exception) { /* A failed optional probe has no live connection to dispose. */ }
    }
}

internal sealed class RealtimeHealthCheck(IOptions<RealtimeOptions> options, RealtimeBackplaneProbe? probe = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Value.Mode == RealtimeMode.Disabled) return HealthCheckResult.Healthy("Live realtime is disabled; HTTP history is enabled.");
        if (options.Value.Mode == RealtimeMode.LocalOnly) return HealthCheckResult.Healthy("Live realtime uses the local process; HTTP history is enabled.");
        try
        {
            if (probe is not null && await probe.CheckAsync(cancellationToken))
                return HealthCheckResult.Healthy("Shared realtime transport connectivity probe succeeded.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        return HealthCheckResult.Degraded("Shared realtime transport connectivity is unavailable; HTTP reconciliation remains enabled.");
    }
}
