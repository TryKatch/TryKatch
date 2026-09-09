using TrykatchApp.Application.Outbox;
using TrykatchApp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace TrykatchApp.UnitTests;

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
            Type = "TrykatchApp.ProjectChanged",
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

    [TestMethod]
    public async Task FailureTelemetryDoesNotIncludeTheTransportExceptionMessage()
    {
        const string plantedSecret = "password=planted-secret";
        RecordingLogger logger = new();
        OutboxDelivery delivery = new(
            new RecordingTransport { Failure = new InvalidOperationException(plantedSecret) },
            TimeProvider.System,
            logger);
        OutboxMessage message = new()
        {
            Id = Guid.CreateVersion7(),
            Type = "TrykatchApp.ProjectChanged",
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        };

        (await delivery.DeliverAsync(message, CancellationToken.None)).ShouldBeFalse();
        logger.Exception.ShouldBeNull();
        logger.StateText.ShouldNotContain(plantedSecret);
        logger.StateText.ShouldContain(typeof(InvalidOperationException).FullName!);
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

    private sealed class RecordingLogger : ILogger<OutboxDelivery>
    {
        public Exception? Exception { get; private set; }
        public string StateText { get; private set; } = string.Empty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
            StateText = formatter(state, exception);
        }
    }
}
