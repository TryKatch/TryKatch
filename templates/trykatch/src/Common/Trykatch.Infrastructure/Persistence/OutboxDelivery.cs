using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Trykatch.Application.Outbox;

namespace Trykatch.Infrastructure.Persistence;

internal sealed partial class OutboxDelivery(
    IOutboxTransport transport,
    TimeProvider timeProvider,
    ILogger<OutboxDelivery> logger)
{
    private static readonly ActivitySource ActivitySource = new("Trykatch.Outbox");
    private static readonly Meter Meter = new("Trykatch.Outbox");
    private static readonly Counter<long> DispatchCounter = Meter.CreateCounter<long>("trykatch.outbox.dispatches");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("trykatch.outbox.dispatch.duration", "s");

    public async Task<bool> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.ProcessedAt is not null) return false;

        long started = Stopwatch.GetTimestamp();
        using Activity? activity = ActivitySource.StartActivity("outbox publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "trykatch.outbox");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("messaging.destination.name", message.Type);

        try
        {
            await transport.PublishAsync(
                new OutboxEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt, message.Attempts + 1),
                cancellationToken);
            message.ProcessedAt = timeProvider.GetUtcNow();
            message.LastErrorCode = null;
            message.LastErrorType = null;
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
            string exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            message.LastErrorCode = Classify(exception);
            message.LastErrorType = exceptionType;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exceptionType);
            DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
            LogFailed(logger, message.Id, message.Attempts, exceptionType);
            return false;
        }
        finally
        {
            DispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private static string Classify(Exception exception) => exception switch
    {
        TimeoutException or OperationCanceledException => "transport_timeout",
        HttpRequestException => "transport_unavailable",
        _ => "transport_failure"
    };

    [LoggerMessage(EventId = 4201, Level = LogLevel.Error, Message = "Outbox message {MessageId} failed on attempt {DeliveryAttempt} with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int deliveryAttempt, string exceptionType);
}
