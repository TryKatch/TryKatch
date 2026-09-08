using FlatpackApp.Application.Outbox;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class OutboxDeliveryTests
{
    [TestMethod]
    public async Task RetriesKeepTheIdempotencyKeyAndProcessedMessagesAreNotRepublished()
    {
        DateTimeOffset completedAt = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);
        RecordingTransport transport = new() { Failure = new InvalidOperationException("temporary failure") };
        OutboxDelivery delivery = new(transport, new FixedTimeProvider(completedAt), NullLogger<OutboxDelivery>.Instance);
        OutboxMessage message = new()
        {
            Id = Guid.CreateVersion7(),
            Type = "FlatpackApp.ProjectChanged",
            Payload = "{\"change\":\"created\"}",
            OccurredAt = completedAt.AddMinutes(-1)
        };

        (await delivery.DeliverAsync(message, CancellationToken.None)).ShouldBeFalse();
        message.Attempts.ShouldBe(1);
        message.ProcessedAt.ShouldBeNull();
        message.LastError.ShouldBe("temporary failure");

        transport.Failure = null;
        (await delivery.DeliverAsync(message, CancellationToken.None)).ShouldBeTrue();
        message.ProcessedAt.ShouldBe(completedAt);
        message.LastError.ShouldBeNull();
        (await delivery.DeliverAsync(message, CancellationToken.None)).ShouldBeFalse();

        transport.Envelopes.Count.ShouldBe(2);
        transport.Envelopes.Select(envelope => envelope.MessageId).Distinct().ShouldHaveSingleItem().ShouldBe(message.Id);
        transport.Envelopes.Select(envelope => envelope.DeliveryAttempt).ShouldBe([1, 2]);
    }

    private sealed class RecordingTransport : IOutboxTransport
    {
        public List<OutboxEnvelope> Envelopes { get; } = [];
        public Exception? Failure { get; set; }

        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
