using System.Diagnostics;
using System.Diagnostics.Metrics;
using FlatpackApp.Application.Outbox;
using Microsoft.Extensions.Logging;

namespace FlatpackApp.Infrastructure.Persistence;

internal sealed partial class OutboxDelivery(
    IOutboxTransport transport,
    TimeProvider timeProvider,
    ILogger<OutboxDelivery> logger)
{
    private const int MaximumErrorLength = 2000;
    private static readonly ActivitySource ActivitySource = new("FlatpackApp.Outbox");
    private static readonly Meter Meter = new("FlatpackApp.Outbox");
    private static readonly Counter<long> DispatchCounter = Meter.CreateCounter<long>("flatpack.outbox.dispatches");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("flatpack.outbox.dispatch.duration", "s");

    public async Task<bool> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.ProcessedAt is not null) return false;

        long started = Stopwatch.GetTimestamp();
        using Activity? activity = ActivitySource.StartActivity("outbox publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "flatpack.outbox");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("messaging.destination.name", message.Type);

        try
        {
            await transport.PublishAsync(
                new OutboxEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt, message.Attempts + 1),
                cancellationToken);
            message.ProcessedAt = timeProvider.GetUtcNow();
            message.LastError = null;
            DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            message.Attempts++;
            message.LastError = exception.Message.Length <= MaximumErrorLength
                ? exception.Message
                : exception.Message[..MaximumErrorLength];
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
            LogFailed(logger, exception, message.Id, message.Attempts);
            return false;
        }
        finally
        {
            DispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Error, Message = "Outbox message {MessageId} failed on attempt {DeliveryAttempt}")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid messageId, int deliveryAttempt);
}
