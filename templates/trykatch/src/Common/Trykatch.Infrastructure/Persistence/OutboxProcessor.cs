using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Trykatch.Infrastructure.Persistence;

internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxRecoveryOptions> options,
    OutboxWorkerState state,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Trykatch.Outbox");
    private static readonly Counter<long> DatabaseCounter = Meter.CreateCounter<long>("trykatch.outbox.database.cycles");
    private readonly ObservableGauge<long> pendingGauge = Meter.CreateObservableGauge(
        "trykatch.outbox.pending.count", () => state.Read().Pending);
    private readonly ObservableGauge<long> terminalGauge = Meter.CreateObservableGauge(
        "trykatch.outbox.terminal.count", () => state.Read().Terminal);
    private readonly ObservableGauge<double> oldestGauge = Meter.CreateObservableGauge(
        "trykatch.outbox.oldest.age", () => state.Read().Oldest is { } oldest
            ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds) : 0, "s");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OutboxRetryBackoff backoff = new(options.Value);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await state.WaitForTransportAsync(stoppingToken);
                await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
                {
                    OutboxBatchProcessor processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
                    await processor.ProcessAsync(stoppingToken);
                }
                DatabaseCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                backoff.Reset();
                await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                string type = exception.GetType().FullName ?? exception.GetType().Name;
                OutboxDatabaseFaultKind kind = OutboxDatabaseFaultClassifier.Classify(exception);
                state.DatabaseFailure(type, kind == OutboxDatabaseFaultKind.Permanent, timeProvider.GetUtcNow());
                DatabaseCounter.Add(1, new KeyValuePair<string, object?>("outcome",
                    kind == OutboxDatabaseFaultKind.Permanent ? "permanent_failure" : "transient_failure"));
                LogDatabaseFailure(logger, type, kind.ToString().ToLowerInvariant());
                if (kind == OutboxDatabaseFaultKind.Permanent)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, stoppingToken);
                    break;
                }
                await Task.Delay(backoff.NextDelay(), timeProvider, stoppingToken);
            }
        }
    }

    [LoggerMessage(EventId = 4210, Level = LogLevel.Error,
        Message = "Outbox database cycle failed with {ExceptionType} classified as {Outcome}")]
    private static partial void LogDatabaseFailure(ILogger logger, string exceptionType, string outcome);
}
