using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trykatch.Application.Execution;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Eventing;

internal sealed partial class ModuleEventDeliveryProcessor(
    IModuleEventDeliveryStore store, OrganizationWorkExecutor executor, TimeProvider clock, ILogger<ModuleEventDeliveryProcessor> logger)
{
    private static readonly Meter Meter = new("Trykatch.ModuleEvents");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("trykatch.module_events.outcomes");

    internal async Task<ModuleExecutionOutcome> ProcessAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        ModuleEventDeliveryClaim? claim = await store.ClaimAsync(deliveryId, cancellationToken);
        return claim is null ? new(ModuleExecutionState.Paused, "delivery_not_ready") : await ExecuteAsync(claim, cancellationToken);
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        int completed = 0;
        for (int count = 0; count < 20; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleEventDeliveryClaim? claim = await store.ClaimAsync(null, cancellationToken);
            if (claim is null) break;
            ModuleExecutionOutcome outcome = await ExecuteAsync(claim, cancellationToken);
            if (outcome.State is ModuleExecutionState.Completed or ModuleExecutionState.AlreadyCompleted) completed++;
        }
        return completed;
    }

    private async Task<ModuleExecutionOutcome> ExecuteAsync(ModuleEventDeliveryClaim claim, CancellationToken cancellationToken)
    {
        if (claim.Outcome is not null) return claim.Outcome;
        ModuleEventDelivery delivery = claim.Delivery ?? throw new InvalidOperationException("Delivery claim is empty.");
        Guid lease = claim.LeaseId ?? throw new InvalidOperationException("Delivery claim has no lease.");
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(1), clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        ModuleExecutionOutcome outcome;
        try { outcome = await executor.ExecuteEventAsync(delivery, claim.RecoveryOnly, linked.Token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(delivery.Id, lease);
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { outcome = Retry("execution_timeout"); }
        catch (PostgresException exception) when (exception.SqlState is "42501" or "42P01" or "42703" or "28P01" or "28000")
        { outcome = new(ModuleExecutionState.TerminalFailure, "runtime_database_contract_invalid"); }
        catch (Exception exception)
        {
            LogFailure(logger, delivery.Id, exception.GetType().FullName ?? exception.GetType().Name);
            outcome = Retry("event_execution_failed");
        }
        Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome.State.ToString()));
        try
        {
            return await store.FinishAsync(delivery.Id, lease, outcome, cancellationToken)
                ? outcome : Retry("lease_lost");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(delivery.Id, lease);
            throw;
        }
        catch (Exception) { return Retry("acknowledgement_failed"); }
    }

    private async Task ReleaseAsync(Guid id, Guid lease)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
        try { await store.ReleaseAsync(id, lease, cleanup.Token); }
        catch (Exception) { /* The token lease expires if bounded cleanup cannot reach the database. */ }
    }
    [LoggerMessage(EventId = 4402, Level = LogLevel.Error, Message = "Module event delivery {DeliveryId} failed with {ExceptionType}")]
    private static partial void LogFailure(ILogger logger, Guid deliveryId, string exceptionType);

    private ModuleExecutionOutcome Retry(string code) => new(ModuleExecutionState.RetryableFailure, code,
        clock.GetUtcNow() + ModuleEventDeliveryStore.RetryDelay);
}

internal sealed record ModuleEventWorkerSnapshot(long Pending, long Retrying, long Terminal, DateTimeOffset? Oldest,
    bool Unavailable = false, bool Unconfigured = false);

internal sealed class ModuleEventWorkerState
{
    private readonly object gate = new();
    private ModuleEventWorkerSnapshot snapshot = new(0, 0, 0, null);
    public ModuleEventWorkerSnapshot Read() { lock (gate) return snapshot; }
    public void Observe(ModuleEventBacklog backlog)
    { lock (gate) snapshot = new(backlog.Pending, backlog.Retrying, backlog.Terminal, backlog.Oldest); }
    public void Unavailable() { lock (gate) snapshot = snapshot with { Unavailable = true }; }
    public void Unconfigured() { lock (gate) snapshot = snapshot with { Unconfigured = true }; }
}

internal sealed class ModuleEventHealthCheck(ModuleEventWorkerState state, TimeProvider clock) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ModuleEventWorkerSnapshot value = state.Read();
        Dictionary<string, object> data = new()
        {
            ["pendingCount"] = value.Pending, ["retryingCount"] = value.Retrying, ["terminalCount"] = value.Terminal,
            ["oldestAgeSeconds"] = value.Oldest is { } oldest ? Math.Max(0, (clock.GetUtcNow() - oldest).TotalSeconds) : 0
        };
        return Task.FromResult(value.Unavailable
            ? HealthCheckResult.Unhealthy("Module event coordination is unavailable.", data: data)
            : value.Terminal > 0 ? HealthCheckResult.Degraded("Module event deliveries require inspection.", data: data)
            : HealthCheckResult.Healthy(value.Unconfigured ? "Module event delivery has no configured consumers or coordinator."
                : "Module event coordination is available.", data));
    }
}

internal sealed partial class ModuleEventDeliveryWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ModuleCatalog catalog, ModuleEventWorkerState state, TimeProvider clock, ILogger<ModuleEventDeliveryWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Trykatch.ModuleEvents");
    private static readonly Histogram<long> Backlog = Meter.CreateHistogram<long>("trykatch.module_events.backlog");
    private static readonly Histogram<double> Oldest = Meter.CreateHistogram<double>("trykatch.module_events.oldest_age", "s");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.GetConnectionString(RuntimeDatabaseConnectionContract.WorkCoordinator) is null
            && !catalog.Descriptors.SelectMany(module => module.EventConsumers).Any())
        {
            state.Unconfigured();
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ModuleEventDeliveryProcessor>().ProcessBatchAsync(stoppingToken);
                ModuleEventBacklog backlog = await scope.ServiceProvider.GetRequiredService<IModuleEventDeliveryStore>().ReadBacklogAsync(stoppingToken);
                state.Observe(backlog);
                Backlog.Record(backlog.Pending, new KeyValuePair<string, object?>("state", "pending"));
                Backlog.Record(backlog.Retrying, new KeyValuePair<string, object?>("state", "retrying"));
                Backlog.Record(backlog.Terminal, new KeyValuePair<string, object?>("state", "terminal"));
                Oldest.Record(backlog.Oldest is { } oldest ? Math.Max(0, (clock.GetUtcNow() - oldest).TotalSeconds) : 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                state.Unavailable();
                LogUnavailable(logger, exception.GetType().FullName ?? exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(EventId = 4401, Level = LogLevel.Error, Message = "Module event worker unavailable with {ExceptionType}")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType);
}
