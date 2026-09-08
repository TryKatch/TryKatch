using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlatpackApp.Infrastructure.Persistence;

internal sealed partial class OutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("FlatpackApp.Outbox");
    private static readonly Meter Meter = new("FlatpackApp.Outbox");
    private static readonly Counter<long> DispatchCounter = Meter.CreateCounter<long>("flatpack.outbox.dispatches");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("flatpack.outbox.dispatch.duration", "s");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessBatchAsync(stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            OutboxMessage[] messages = await dbContext.OutboxMessages
                .FromSqlRaw("""
                    SELECT * FROM platform.outbox_messages
                    WHERE "ProcessedAt" IS NULL AND "Attempts" < 10
                    ORDER BY "OccurredAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 50
                    """)
                .ToArrayAsync(cancellationToken);

            foreach (OutboxMessage message in messages)
            {
                long started = Stopwatch.GetTimestamp();
                using Activity? activity = ActivitySource.StartActivity("outbox dispatch", ActivityKind.Producer);
                activity?.SetTag("messaging.system", "flatpack.outbox");
                activity?.SetTag("messaging.message.id", message.Id);
                activity?.SetTag("messaging.destination.name", message.Type);
                try
                {
                    LogDispatched(logger, message.Id, message.Type);
                    message.ProcessedAt = DateTimeOffset.UtcNow;
                    message.LastError = null;
                    DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                }
                catch (Exception exception)
                {
                    message.Attempts++;
                    message.LastError = exception.Message;
                    activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                    activity?.AddException(exception);
                    DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
                    LogFailed(logger, exception, message.Id);
                }
                finally
                {
                    DispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                }
            }

            if (messages.Length > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({MessageType}) dispatched")]
    private static partial void LogDispatched(ILogger logger, Guid messageId, string messageType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid messageId);
}
