#if TRYKATCH_EMAIL
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QueuedEmailDispatcherTests
{
    private static readonly string[] SafeTraceKeys = ["email.delivery.boundary", "email.delivery.outcome", "messaging.message.id"];
    private static readonly string[] SafeMetricKeys = ["boundary", "outcome"];
    [TestMethod]
    [DoNotParallelize]
    public async Task AttemptTelemetryCorrelatesOpaqueDeliveryWithoutPrivateContentOrHighCardinalityMetricTags()
    {
        List<Activity> activities = [];
        List<KeyValuePair<string, object?>[]> measurements = [];
        using ActivityListener tracing = new()
        {
            ShouldListenTo = source => source.Name == "Trykatch.EmailDelivery",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(tracing);
        using MeterListener metrics = new();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Trykatch.EmailDelivery") listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        metrics.Start();
        TestSource source = new(includeDefaultClaim: false);
        EmailDeliveryLease lease = Lease() with { ProtectedIntent = "planted-ciphertext-marker" };
        source.Claims.Enqueue([lease]);
        source.Prepare = (_, _) => Task.FromResult(new EmailDeliveryPreparation(EmailPreparationDisposition.Ready,
            new("planted-recipient@example.invalid", "planted-subject", "planted-body-token", "planted-private-html")));
        using Harness harness = new(source);
        await harness.Dispatcher.DispatchOnceAsync();

        Activity activity = activities.ShouldHaveSingleItem();
        activity.GetTagItem("messaging.message.id").ShouldBe(lease.Context.DeliveryId);
        activity.GetTagItem("email.delivery.outcome").ShouldBe("acknowledged");
        activity.TagObjects.Select(value => value.Key).Order().ShouldBe(SafeTraceKeys.Order());
        foreach (KeyValuePair<string, object?> tag in activity.TagObjects) (tag.Value?.ToString() ?? "").ShouldNotContain("planted");
        measurements.ShouldNotBeEmpty();
        foreach (KeyValuePair<string, object?>[] tags in measurements)
        {
            tags.Select(value => value.Key).Order().ShouldBe(SafeMetricKeys.Order());
            foreach (KeyValuePair<string, object?> tag in tags) (tag.Value?.ToString() ?? "").ShouldNotContain("planted");
        }
    }

    [TestMethod]
    public async Task SuppressedPreparationCompletesWithoutCallingSmtp()
    {
        using Harness harness = new(new TestSource
        {
            Prepare = (_, _) => Task.FromResult(new EmailDeliveryPreparation(
                EmailPreparationDisposition.Suppressed, FailureCode: EmailFailureCode.SourceDenied))
        });

        await harness.Dispatcher.DispatchOnceAsync();

        harness.Transport.SendCount.ShouldBe(0);
        harness.Source.Completed.Single().Disposition.ShouldBe(EmailAttemptDisposition.Suppressed);
        harness.Source.Completed.Single().FailureCode.ShouldBe(EmailFailureCode.SourceDenied);
        harness.Source.ClaimLimits.ShouldAllBe(limit => limit == 1);
    }

    [TestMethod]
    public async Task TransientPreparationFailureIsRecordedAsSourceUnavailableWithoutSmtp()
    {
        foreach (Exception transient in new Exception[]
        {
            new IOException("private source detail"), new TimeoutException("private source detail"), new TestDbException("private source detail")
        })
        {
            using Harness harness = new(new TestSource
            {
                Prepare = (_, _) => Task.FromException<EmailDeliveryPreparation>(transient)
            });

            await harness.Dispatcher.DispatchOnceAsync();

            harness.Transport.SendCount.ShouldBe(0);
            harness.Source.Completed.Single().ShouldBe(new EmailAttemptOutcome(
                EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SourceUnavailable));
        }
    }

    [TestMethod]
    public async Task ReadyPreparationRequiresMessageAndNoFailureCode()
    {
        foreach (EmailDeliveryPreparation invalid in new[]
        {
            new EmailDeliveryPreparation(EmailPreparationDisposition.Ready),
            new EmailDeliveryPreparation(EmailPreparationDisposition.Ready, ReadyMessage, EmailFailureCode.SourceDenied)
        })
        {
            using Harness harness = new(new TestSource
            {
                Prepare = (_, _) => Task.FromResult(invalid)
            });

            await harness.Dispatcher.DispatchOnceAsync();

            harness.Transport.SendCount.ShouldBe(0);
            harness.Source.Completed.Single().Disposition.ShouldBe(EmailAttemptDisposition.PermanentFailure);
            harness.Source.Completed.Single().FailureCode.ShouldBe(EmailFailureCode.PayloadInvalid);
        }
    }

    [TestMethod]
    public async Task AcknowledgementIsCompletedWithIndependentTokenAfterCallerCancellation()
    {
        TestTransport transport = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<EmailTransportResult> finishSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Send = async (_, _) =>
        {
            started.TrySetResult();
            return await finishSend.Task;
        };
        using Harness harness = new(new TestSource(), transport);
        using CancellationTokenSource callerCancellation = new();

        Task dispatch = harness.Dispatcher.DispatchOnceAsync(callerCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        finishSend.SetResult(new EmailTransportResult(EmailTransportDisposition.Acknowledged));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));

        harness.Source.Completed.Single().Disposition.ShouldBe(EmailAttemptDisposition.Acknowledged);
        harness.Source.CompletionTokens.Single().CanBeCanceled.ShouldBeTrue();
        harness.Source.CompletionTokens.Single().IsCancellationRequested.ShouldBeFalse();
    }

    [TestMethod]
    public async Task TransientCompletionFailureRetriesExactLeaseAndOutcomeWithoutResending()
    {
        int completionAttempts = 0;
        TestSource source = new()
        {
            Complete = (_, _, _) =>
            {
                if (Interlocked.Increment(ref completionAttempts) == 1)
                    return Task.FromException<bool>(new TimeoutException("private store detail"));
                return Task.FromResult(true);
            }
        };
        using Harness harness = new(source, new TestTransport
        {
            Send = (_, _) => Task.FromResult(new EmailTransportResult(
                EmailTransportDisposition.RetryableFailure, EmailTransportStage.Recipient,
                EmailTransportFailureCode.SmtpRejected, 450))
        });

        await harness.Dispatcher.DispatchOnceAsync();

        harness.Transport.SendCount.ShouldBe(1);
        source.Completed.Count.ShouldBe(2);
        source.CompletionCalls.Count.ShouldBe(2);
        source.CompletionCalls[0].Lease.ShouldBeSameAs(source.CompletionCalls[1].Lease);
        source.CompletionCalls[0].Outcome.ShouldBe(source.CompletionCalls[1].Outcome);
        source.Completed.ShouldAllBe(outcome => outcome == new EmailAttemptOutcome(
            EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SmtpRejected, 450));
    }

    [TestMethod]
    public async Task PreparationIsRecheckedOnEachLeasedAttempt()
    {
        TestSource source = new(includeDefaultClaim: false);
        source.Claims.Enqueue([Lease(attempt: 1)]);
        source.Claims.Enqueue([Lease(attempt: 2)]);
        int preparations = 0;
        source.Prepare = (_, _) =>
        {
            preparations++;
            return Task.FromResult(new EmailDeliveryPreparation(EmailPreparationDisposition.Ready, ReadyMessage));
        };
        TestTransport transport = new();
        transport.Results.Enqueue(new(EmailTransportDisposition.RetryableFailure,
            EmailTransportStage.Connect, EmailTransportFailureCode.ConnectionFailed));
        transport.Results.Enqueue(new(EmailTransportDisposition.Acknowledged));
        using Harness harness = new(source, transport);

        await harness.Dispatcher.DispatchOnceAsync();
        await harness.Dispatcher.DispatchOnceAsync();

        preparations.ShouldBe(2);
        transport.SendCount.ShouldBe(2);
        source.Completed.Select(outcome => outcome.Disposition).ShouldBe([
            EmailAttemptDisposition.RetryableFailure, EmailAttemptDisposition.Acknowledged]);
    }

    [TestMethod]
    public async Task PreparationDeadlineReservesTimeForCompletionBeforeLeaseExpiry()
    {
        TaskCompletionSource<CancellationToken> preparationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestSource source = new(includeDefaultClaim: false)
        {
            Prepare = async (_, token) =>
            {
                preparationStarted.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }
        };
        source.Claims.Enqueue([Lease(TimeSpan.FromSeconds(5.3))]);
        using Harness harness = new(source);
        using CancellationTokenSource safety = new(TimeSpan.FromSeconds(3));
        Stopwatch elapsed = Stopwatch.StartNew();

        Task dispatch = harness.Dispatcher.DispatchOnceAsync(safety.Token);
        CancellationToken attemptToken = await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        attemptToken.CanBeCanceled.ShouldBeTrue();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        harness.Transport.SendCount.ShouldBe(0);
        harness.Source.Completed.Single().ShouldBe(new EmailAttemptOutcome(
            EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SourceUnavailable));
    }

    [TestMethod]
    public async Task LeaseWithoutCompletionHeadroomDoesNotPrepareOrSend()
    {
        TestSource source = new(includeDefaultClaim: false)
        {
            Prepare = (_, _) => throw new InvalidOperationException("Preparation must not start.")
        };
        source.Claims.Enqueue([Lease(TimeSpan.FromSeconds(4))]);
        using Harness harness = new(source);

        await harness.Dispatcher.DispatchOnceAsync();

        harness.Transport.SendCount.ShouldBe(0);
        source.Completed.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task WorkerStopsOnUnexpectedFailureWithoutExposingPrivateExceptionToHostLogging()
    {
        const string privateDetail = "recipient@example.test reset-token-private body-private";
        using Harness harness = new(new TestSource
        {
            Prepare = (_, _) => Task.FromException<EmailDeliveryPreparation>(new InvalidOperationException(privateDetail))
        });
        using QueuedEmailDeliveryWorker worker = new(harness.Dispatcher, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        Exception failure = await Should.ThrowAsync<Exception>(
            () => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));

        failure.ToString().ShouldNotContain(privateDetail);
        failure.Message.ShouldBe("Queued email delivery stopped because an adapter failed.");
        failure.InnerException.ShouldBeNull();
        harness.Transport.SendCount.ShouldBe(0);
        harness.Source.Completed.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task UnclassifiedTransportExceptionIsNotConvertedIntoAcknowledgement()
    {
        using Harness harness = new(new TestSource(), new TestTransport
        {
            Send = (_, _) => Task.FromException<EmailTransportResult>(new InvalidOperationException("private transport detail"))
        });

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Dispatcher.DispatchOnceAsync());

        failure.Message.ShouldBe("private transport detail");
        harness.Source.Completed.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task EachDispatchPassResolvesFreshScopedSourcesAndTransport()
    {
        List<TestSource> resolvedSources = [];
        List<TestTransport> resolvedTransports = [];
        ServiceCollection registrations = new();
        registrations.AddScoped<ScopeMarker>();
        registrations.AddScoped<IEmailDeliverySource>(services =>
        {
            TestSource source = new() { ScopeMarker = services.GetRequiredService<ScopeMarker>() };
            resolvedSources.Add(source);
            return source;
        });
        registrations.AddScoped<IEmailTransport>(_ =>
        {
            TestTransport transport = new();
            resolvedTransports.Add(transport);
            return transport;
        });
        using ServiceProvider services = registrations.BuildServiceProvider();
        QueuedEmailDispatcher dispatcher = new(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        resolvedSources.Count.ShouldBe(2);
        resolvedSources[0].ShouldNotBeSameAs(resolvedSources[1]);
        resolvedSources[0].ScopeMarker.ShouldNotBeSameAs(resolvedSources[1].ScopeMarker);
        resolvedTransports.Count.ShouldBe(2);
        resolvedTransports[0].ShouldNotBeSameAs(resolvedTransports[1]);
    }

    private static readonly EmailPreparedMessage ReadyMessage = new(
        "recipient@example.test", "subject", "body", null);

    private static EmailDeliveryLease Lease(TimeSpan? duration = null, int attempt = 1) => new(
        new EmailDeliveryProtectionContext(EmailDeliveryBoundary.Organization, Guid.NewGuid(), Guid.NewGuid(), "core.invitation", 1),
        Guid.NewGuid(), attempt, DateTimeOffset.UtcNow.Add(duration ?? TimeSpan.FromMinutes(1)), "protected-fixture");

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider services;
        public QueuedEmailDispatcher Dispatcher { get; }
        public TestSource Source { get; }
        public TestTransport Transport { get; }

        public Harness(TestSource? source = null, TestTransport? transport = null)
        {
            Source = source ?? new TestSource();
            Transport = transport ?? new TestTransport();
            services = new ServiceCollection()
                .AddScoped<IEmailDeliverySource>(_ => Source)
                .AddScoped<IEmailTransport>(_ => Transport)
                .BuildServiceProvider();
            Dispatcher = new QueuedEmailDispatcher(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        }

        public void Dispose() => services.Dispose();
    }

    private sealed class TestSource : IEmailDeliverySource
    {
        public TestSource(bool includeDefaultClaim = true)
        {
            if (includeDefaultClaim)
                Claims.Enqueue([Lease()]);
        }

        public EmailDeliveryBoundary Boundary => EmailDeliveryBoundary.Organization;
        public ScopeMarker? ScopeMarker { get; init; }
        public Queue<IReadOnlyList<EmailDeliveryLease>> Claims { get; } = new();
        public List<int> ClaimLimits { get; } = [];
        public List<EmailAttemptOutcome> Completed { get; } = [];
        public List<CancellationToken> CompletionTokens { get; } = [];
        public List<(EmailDeliveryLease Lease, EmailAttemptOutcome Outcome)> CompletionCalls { get; } = [];
        public Func<EmailDeliveryLease, CancellationToken, Task<EmailDeliveryPreparation>> Prepare { get; set; } =
            (_, _) => Task.FromResult(new EmailDeliveryPreparation(EmailPreparationDisposition.Ready, ReadyMessage));
        public Func<EmailDeliveryLease, EmailAttemptOutcome, CancellationToken, Task<bool>> Complete { get; set; } =
            (_, _, _) => Task.FromResult(true);

        public Task<IReadOnlyList<EmailDeliveryLease>> ClaimAsync(int limit, CancellationToken cancellationToken)
        {
            ClaimLimits.Add(limit);
            return Task.FromResult(Claims.Count == 0 ? (IReadOnlyList<EmailDeliveryLease>)[] : Claims.Dequeue());
        }

        public Task<EmailDeliveryPreparation> PrepareAsync(EmailDeliveryLease lease, CancellationToken cancellationToken) =>
            Prepare(lease, cancellationToken);

        public async Task<bool> CompleteAsync(EmailDeliveryLease lease, EmailAttemptOutcome outcome, CancellationToken cancellationToken)
        {
            CompletionCalls.Add((lease, outcome));
            CompletionTokens.Add(cancellationToken);
            Completed.Add(outcome);
            return await Complete(lease, outcome, cancellationToken);
        }
    }

    private sealed class TestTransport : IEmailTransport
    {
        public int SendCount { get; private set; }
        public Queue<EmailTransportResult> Results { get; } = new();
        public Func<EmailMessage, CancellationToken, Task<EmailTransportResult>> Send { get; set; } =
            (_, _) => Task.FromResult(new EmailTransportResult(EmailTransportDisposition.Acknowledged));

        public Task<EmailTransportResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Results.Count == 0
                ? Send(message, cancellationToken)
                : Task.FromResult(Results.Dequeue());
        }
    }

    private sealed class ScopeMarker { }

    private sealed class TestDbException(string message) : DbException(message) { }
}
#endif
