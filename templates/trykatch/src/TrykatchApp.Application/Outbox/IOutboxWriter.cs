namespace TrykatchApp.Application.Outbox;

public interface IOutboxWriter
{
    void Enqueue<T>(T message) where T : notnull;
}

public sealed record OutboxEnvelope(
    Guid MessageId,
    string MessageType,
    string Payload,
    DateTimeOffset OccurredAt,
    int DeliveryAttempt);

/// <summary>
/// Publishes an outbox envelope. Delivery is at least once; adapters and consumers
/// must use <see cref="OutboxEnvelope.MessageId"/> as their idempotency key.
/// </summary>
public interface IOutboxTransport
{
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}
