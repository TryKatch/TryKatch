using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trykatch.Infrastructure.Quotas;

internal sealed record QuotaMaintenanceBacklog(long Pending, long Retrying, long Paused, DateTimeOffset? Oldest);
internal sealed record QuotaMaintenanceSnapshot(QuotaMaintenanceBacklog Backlog, bool Unavailable);

internal sealed class QuotaMaintenanceWorkerState
{
    private readonly object gate = new();
    private QuotaMaintenanceSnapshot snapshot = new(new(0, 0, 0, null), true);
    internal QuotaMaintenanceSnapshot Read() { lock (gate) return snapshot; }
    internal void Observe(QuotaMaintenanceBacklog backlog) { lock (gate) snapshot = new(backlog, false); }
    internal void Unavailable() { lock (gate) snapshot = snapshot with { Unavailable = true }; }
}

internal sealed class QuotaMaintenanceHealthCheck(QuotaMaintenanceWorkerState state, TimeProvider clock) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QuotaMaintenanceSnapshot snapshot = state.Read();
        QuotaMaintenanceBacklog backlog = snapshot.Backlog;
        double oldestAge = backlog.Oldest is { } oldest ? Math.Max(0, (clock.GetUtcNow() - oldest).TotalSeconds) : 0;
        Dictionary<string, object> data = new()
        {
            ["pendingCount"] = backlog.Pending, ["retryingCount"] = backlog.Retrying,
            ["pausedCount"] = backlog.Paused, ["oldestAgeSeconds"] = oldestAge
        };
        return Task.FromResult(snapshot.Unavailable
            ? HealthCheckResult.Unhealthy("Quota maintenance is unavailable.", data: data)
            : backlog.Retrying > 0 || backlog.Paused > 0 || oldestAge >= 300
                ? HealthCheckResult.Degraded("Quota maintenance has retained work requiring attention.", data: data)
                : HealthCheckResult.Healthy("Quota maintenance is available.", data));
    }
}

internal sealed partial class QuotaMaintenanceWorker(
    IServiceScopeFactory scopes, QuotaMaintenanceWorkerState state, TimeProvider clock,
    ILogger<QuotaMaintenanceWorker> logger) : BackgroundService
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Trykatch.QuotaMaintenance");
    private static readonly System.Diagnostics.Metrics.Histogram<long> Backlog = Meter.CreateHistogram<long>("trykatch.quota_maintenance.backlog");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                QuotaMaintenanceStore store = scope.ServiceProvider.GetRequiredService<QuotaMaintenanceStore>();
                state.Observe(await store.ReadBacklogAsync(stoppingToken));
                await scope.ServiceProvider.GetRequiredService<QuotaMaintenanceProcessor>().ProcessBatchAsync(stoppingToken);
                QuotaMaintenanceBacklog backlog = await store.ReadBacklogAsync(stoppingToken);
                state.Observe(backlog);
                Backlog.Record(backlog.Pending, new KeyValuePair<string, object?>("state", "pending"));
                Backlog.Record(backlog.Retrying, new KeyValuePair<string, object?>("state", "retrying"));
                Backlog.Record(backlog.Paused, new KeyValuePair<string, object?>("state", "paused"));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception failure)
            {
                state.Unavailable();
                LogUnavailable(logger, failure.GetType().FullName ?? failure.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(EventId = 4601, Level = LogLevel.Error, Message = "Quota maintenance coordination failed with {ExceptionType}")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType);
}
