using FlatpackApp.Application.Outbox;
using Microsoft.Extensions.Logging;

namespace FlatpackApp.Infrastructure.Persistence;

/// <summary>
/// Safe default adapter for a template without a message broker. Replace this
/// registration with a broker adapter when integration events leave the process.
/// </summary>
internal sealed partial class LoggingOutboxTransport(ILogger<LoggingOutboxTransport> logger) : IOutboxTransport
{
    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogPublished(logger, envelope.MessageId, envelope.MessageType, envelope.DeliveryAttempt);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "Outbox message {MessageId} ({MessageType}) published on attempt {DeliveryAttempt}")]
    private static partial void LogPublished(ILogger logger, Guid messageId, string messageType, int deliveryAttempt);
}
