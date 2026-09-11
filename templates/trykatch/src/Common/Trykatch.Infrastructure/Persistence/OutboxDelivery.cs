using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Trykatch.Application.Outbox;

namespace Trykatch.Infrastructure.Persistence;

internal sealed partial class OutboxDelivery(
    IOutboxTransport transport,
    TimeProvider timeProvider,
    OutboxWorkerState state,
    ILogger<OutboxDelivery> logger)
{
    private static readonly ActivitySource ActivitySource = new("Trykatch.Outbox");
    private static readonly Meter Meter = new("Trykatch.Outbox");
    private static readonly Counter<long> DispatchCounter = Meter.CreateCounter<long>("trykatch.outbox.dispatches");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("trykatch.outbox.dispatch.duration", "s");

    public async Task<bool> DeliverAsync(OutboxMessage message, TimeSpan timeout, CancellationToken stoppingToken)
    {
        if (message.ProcessedAt is not null || message.ExhaustedAt is not null) return false;
        await state.WaitForTransportAsync(stoppingToken);
        stoppingToken.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();
        using Activity? activity = ActivitySource.StartActivity("outbox publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "trykatch.outbox");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("messaging.destination.name", message.Type);

        using CancellationTokenSource deadline = new(timeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        try
        {
            Task publish = transport.PublishAsync(
                new OutboxEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt, message.Attempts + 1),
                linked.Token);
            Task wait = Task.Delay(timeout, timeProvider, stoppingToken);
            if (await Task.WhenAny(publish, wait) != publish)
            {
                linked.Cancel();
                state.TransportBlocked(true, timeProvider.GetUtcNow());
                state.TrackTransport(ObserveLatePublicationAsync(publish));
                stoppingToken.ThrowIfCancellationRequested();
                RecordFailure(message, "transport_timeout", typeof(TimeoutException).FullName!, activity);
                return false;
            }

            await publish;
            message.ProcessedAt = timeProvider.GetUtcNow();
            message.LastErrorCode = null;
            message.LastErrorType = null;
            DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            RecordFailure(message, "transport_timeout", typeof(TimeoutException).FullName!, activity);
            return false;
        }
        catch (Exception exception)
        {
            string exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            string code = exception is TimeoutException ? "transport_timeout"
                : exception is HttpRequestException ? "transport_unavailable"
                : "transport_failure";
            RecordFailure(message, code, exceptionType, activity);
            return false;
        }
        finally
        {
            DispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task ObserveLatePublicationAsync(Task publication)
    {
        try
        {
            await publication;
        }
        catch (Exception exception)
        {
            LogLateFault(logger, exception.GetType().FullName ?? exception.GetType().Name);
        }
        finally
        {
            state.TransportBlocked(false, timeProvider.GetUtcNow());
        }
    }

    private void RecordFailure(OutboxMessage message, string code, string type, Activity? activity)
    {
        message.Attempts++;
        message.LastErrorCode = code;
        message.LastErrorType = type.Length <= 240 ? type : type[..240];
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", message.LastErrorType);
        DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", code));
        LogFailed(logger, message.Id, message.Attempts, message.LastErrorType);
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Error, Message = "Outbox message {MessageId} failed on attempt {DeliveryAttempt} with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int deliveryAttempt, string exceptionType);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Error, Message = "Outbox transport completed late with {ExceptionType}")]
    private static partial void LogLateFault(ILogger logger, string exceptionType);
}
