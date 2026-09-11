using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OutboxDeliveryTests
{
    [TestMethod]
    public void MovedIntegrationEventsKeepTheirExistingWireContractNames()
    {
        OutboxContractName.For(typeof(Trykatch.Modules.Projects.IntegrationEvents.ProjectChanged))
            .ShouldBe("TrykatchApp.Application.Projects.ProjectChanged");
        OutboxContractName.For(typeof(Trykatch.Modules.Documents.IntegrationEvents.DocumentChanged))
            .ShouldBe("Try" + "katch.Modules.Documents.DocumentChanged");
    }

    [TestMethod]
    [DataRow("Horizon.Modules.Projects.IntegrationEvents.ProjectChanged", "Horizon.Application.Projects.ProjectChanged")]
    [DataRow("Northwind.Crm.Modules.Projects.IntegrationEvents.ProjectChanged", "Northwind.Crm.Application.Projects.ProjectChanged")]
    [DataRow("Horizon.Modules.Documents.IntegrationEvents.DocumentChanged", "Try" + "katch.Modules.Documents.DocumentChanged")]
    public void GeneratedApplicationsKeepTheirPreviewNineEventName(string currentName, string expectedName)
    {
        OutboxContractName.For(currentName).ShouldBe(expectedName);
    }

    [TestMethod]
    public async Task RetriesKeepTheIdempotencyKeyAndProcessedMessagesAreNotRepublished()
    {
        DateTimeOffset completedAt = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);
        const string plantedSecret = "password=temporary-failure-secret";
        RecordingTransport transport = new() { Failure = new InvalidOperationException(plantedSecret) };
        OutboxDelivery delivery = new(transport, new FixedTimeProvider(completedAt), new OutboxWorkerState(), NullLogger<OutboxDelivery>.Instance);
        OutboxMessage message = new()
        {
            Id = Guid.CreateVersion7(),
            Type = "Trykatch.ProjectChanged",
            Payload = "{\"change\":\"created\"}",
            OccurredAt = completedAt.AddMinutes(-1)
        };

        (await delivery.DeliverAsync(message, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeFalse();
        message.Attempts.ShouldBe(1);
        message.ProcessedAt.ShouldBeNull();
        message.LastErrorCode.ShouldBe("transport_failure");
        message.LastErrorType.ShouldBe(typeof(InvalidOperationException).FullName);
        message.LastErrorCode!.ShouldNotContain(plantedSecret);
        message.LastErrorType!.ShouldNotContain(plantedSecret);

        transport.Failure = null;
        (await delivery.DeliverAsync(message, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeTrue();
        message.ProcessedAt.ShouldBe(completedAt);
        message.LastErrorCode.ShouldBeNull();
        message.LastErrorType.ShouldBeNull();
        (await delivery.DeliverAsync(message, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeFalse();

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
            new OutboxWorkerState(),
            logger);
        OutboxMessage message = new()
        {
            Id = Guid.CreateVersion7(),
            Type = "Trykatch.ProjectChanged",
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        };

        (await delivery.DeliverAsync(message, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeFalse();
        logger.Exception.ShouldBeNull();
        logger.StateText.ShouldNotContain(plantedSecret);
        logger.StateText.ShouldContain(typeof(InvalidOperationException).FullName!);
    }

    [TestMethod]
    public async Task SynchronousTransportFailureUsesTheSameDurableSafeFailureBoundary()
    {
        OutboxMessage message = Message();
        RecordingLogger logger = new();
        OutboxDelivery delivery = new(new SynchronousFailureTransport(), TimeProvider.System,
            new OutboxWorkerState(), logger);

        (await delivery.DeliverAsync(message, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeFalse();

        message.Attempts.ShouldBe(1);
        message.ProcessedAt.ShouldBeNull();
        message.LastErrorCode.ShouldBe("transport_unavailable");
        logger.Exception.ShouldBeNull();
        logger.StateText.ShouldNotContain("synchronous-secret");
    }

    [TestMethod]
    public async Task TimeoutReturnsBoundedFailureAndPreventsOverlappingPublication()
    {
        HangingTransport transport = new();
        OutboxWorkerState state = new();
        OutboxDelivery delivery = new(transport, TimeProvider.System, state, NullLogger<OutboxDelivery>.Instance);
        OutboxMessage message = Message();

        Task<bool> attempt = delivery.DeliverAsync(message, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBeFalse();
        transport.Calls.ShouldBe(1);
        state.Read().TransportInvocationBlocked.ShouldBeTrue();
        Task<bool> next = delivery.DeliverAsync(Message(), TimeSpan.FromSeconds(5), CancellationToken.None);
        next.IsCompleted.ShouldBeFalse();
        transport.Calls.ShouldBe(1);
        transport.Complete();
        (await next.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBeTrue();
        transport.Calls.ShouldBe(2);
        state.Read().TransportInvocationBlocked.ShouldBeFalse();
        message.LastErrorCode.ShouldBe("transport_timeout");
    }

    [TestMethod]
    public async Task ShutdownDoesNotWaitForCancellationIgnoringTransportOrRecordFailure()
    {
        HangingTransport transport = new();
        OutboxWorkerState state = new();
        OutboxDelivery delivery = new(transport, TimeProvider.System, state, NullLogger<OutboxDelivery>.Instance);
        OutboxMessage message = Message();
        using CancellationTokenSource shutdown = new();
        Task<bool> attempt = delivery.DeliverAsync(message, TimeSpan.FromSeconds(30), shutdown.Token);
        shutdown.Cancel();

        try
        {
            await Should.ThrowAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(2)));
            message.Attempts.ShouldBe(0);
            message.LastErrorCode.ShouldBeNull();
            state.Read().TransportInvocationBlocked.ShouldBeTrue();
        }
        finally
        {
            transport.Complete();
        }
    }

    [TestMethod]
    public async Task HostShutdownIsNotPersistedAsTransportTimeout()
    {
        HangingTransport transport = new() { HonorsCancellation = true };
        OutboxDelivery delivery = new(transport, TimeProvider.System, new OutboxWorkerState(), NullLogger<OutboxDelivery>.Instance);
        OutboxMessage message = Message();
        using CancellationTokenSource shutdown = new(TimeSpan.FromMilliseconds(20));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            delivery.DeliverAsync(message, TimeSpan.FromSeconds(5), shutdown.Token));

        message.Attempts.ShouldBe(0);
        message.LastErrorCode.ShouldBeNull();
    }

    private static OutboxMessage Message() => new()
    {
        Id = Guid.CreateVersion7(),
        Type = "Trykatch.ProjectChanged",
        Payload = "{}",
        OccurredAt = DateTimeOffset.UtcNow
    };

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

    private sealed class SynchronousFailureTransport : IOutboxTransport
    {
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) =>
            throw new HttpRequestException("password=synchronous-secret");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class HangingTransport : IOutboxTransport
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool HonorsCancellation { get; init; }
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
        {
            Calls++;
            if (HonorsCancellation) cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }
        public void Complete() => completion.TrySetResult();
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
