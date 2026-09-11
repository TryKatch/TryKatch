using System.Security.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OutboxRecoveryTests
{
    [TestMethod]
    public void DefaultsMatchTheOperationalContract()
    {
        OutboxRecoveryOptions options = new();

        options.BatchSize.ShouldBe(50);
        options.MaximumAttempts.ShouldBe(10);
        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(1));
        options.RetryMaximumDelay.ShouldBe(TimeSpan.FromSeconds(30));
        options.TransportTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.DatabaseCommandTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.DatabaseLockTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        options.BatchWorkBudget.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public void ValidationRejectsEveryBoundaryViolation()
    {
        OutboxRecoveryOptionsValidator validator = new();
        OutboxRecoveryOptions[] invalid =
        [
            new() { BatchSize = 0 },
            new() { MaximumAttempts = 101 },
            new() { PollInterval = TimeSpan.FromMilliseconds(99) },
            new() { RetryBaseDelay = TimeSpan.FromSeconds(31) },
            new() { RetryBaseDelay = TimeSpan.FromSeconds(2), RetryMaximumDelay = TimeSpan.FromSeconds(1) },
            new() { TransportTimeout = TimeSpan.FromSeconds(121) },
            new() { DatabaseCommandTimeout = TimeSpan.Zero },
            new() { DatabaseLockTimeout = TimeSpan.FromSeconds(11) },
            new() { BatchWorkBudget = TimeSpan.FromSeconds(301) }
        ];

        invalid.ShouldAllBe(options => validator.Validate(Options.DefaultName, options).Failed);
        validator.Validate(Options.DefaultName, new()).Succeeded.ShouldBeTrue();
    }

    [TestMethod]
    public void BackoffIsBoundedOverflowSafeAndResetsAfterSuccess()
    {
        OutboxRetryBackoff backoff = new(new OutboxRecoveryOptions
        {
            RetryBaseDelay = TimeSpan.FromSeconds(1),
            RetryMaximumDelay = TimeSpan.FromSeconds(30)
        }, () => 0.5);

        Enumerable.Range(0, 200).Select(_ => backoff.NextDelay())
            .ShouldAllBe(delay => delay >= TimeSpan.FromSeconds(1) && delay <= TimeSpan.FromSeconds(30));

        backoff.Reset();
        backoff.NextDelay().ShouldBe(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void DatabaseFaultClassificationUsesProviderStateNotMessages()
    {
        OutboxDatabaseFaultClassifier.Classify(new PostgresException("password=secret", "FATAL", "FATAL", PostgresErrorCodes.InvalidPassword))
            .ShouldBe(OutboxDatabaseFaultKind.Permanent);
        OutboxDatabaseFaultClassifier.Classify(new PostgresException("safe looking", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure))
            .ShouldBe(OutboxDatabaseFaultKind.Transient);
        OutboxDatabaseFaultClassifier.Classify(new NpgsqlException("password=secret", new IOException()))
            .ShouldBe(OutboxDatabaseFaultKind.Transient);
    }

    [TestMethod]
    public void DatabaseStatementTimeoutIsRecoverableWithoutParsingServerText()
    {
        PostgresException timeout = new("password=untrusted", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled);

        OutboxDatabaseFaultClassifier.Classify(timeout).ShouldBe(OutboxDatabaseFaultKind.Transient);
    }

    [TestMethod]
    public void DatabaseFaultClassificationFindsTransientProviderFaultInsideEfWrapper()
    {
        InvalidOperationException connectionFailure = new(
            "The database operation failed.",
            new NpgsqlException("password=untrusted", new IOException("connection interrupted")));
        InvalidOperationException serializationFailure = new(
            "The database operation failed.",
            new PostgresException("password=untrusted", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure));

        OutboxDatabaseFaultClassifier.Classify(connectionFailure).ShouldBe(OutboxDatabaseFaultKind.Transient);
        OutboxDatabaseFaultClassifier.Classify(serializationFailure).ShouldBe(OutboxDatabaseFaultKind.Transient);
    }

    [TestMethod]
    public void DatabaseTlsAuthenticationFailureIsPermanentEvenInsideTransientIoWrapper()
    {
        NpgsqlException exception = new("secret", new IOException("secret", new AuthenticationException("secret")));

        OutboxDatabaseFaultClassifier.Classify(exception).ShouldBe(OutboxDatabaseFaultKind.Permanent);
    }

    [TestMethod]
    public async Task RetryingExecutionStrategyFailsBeforeExternalPublication()
    {
        CountingTransport transport = new();
        OutboxWorkerState state = new();
        await using OutboxDbContext context = new(new DbContextOptionsBuilder<OutboxDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=never-open;Username=none;Password=none",
                postgres => postgres.EnableRetryOnFailure())
            .Options);
        OutboxBatchProcessor processor = new(context,
            new OutboxDelivery(transport, TimeProvider.System, state, NullLogger<OutboxDelivery>.Instance),
            Options.Create(new OutboxRecoveryOptions()), state, TimeProvider.System);

        (await Should.ThrowAsync<InvalidOperationException>(() => processor.ProcessAsync(CancellationToken.None)))
            .Message.ShouldContain("non-retrying");
        transport.Calls.ShouldBe(0);
    }

    [TestMethod]
    public void RuntimeProfilesSeparateRequestAndWorkerRecoveryPrivileges()
    {
        Dictionary<string, DataResourceDescriptor> resources = [];
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Platform,
            "platform.outbox_replay_requests", resources).ShouldBe(new HashSet<string> { "SELECT", "INSERT" }, true);
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Platform,
            "platform.outbox_recovery_events", resources).ShouldBe(new HashSet<string> { "SELECT" }, true);
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Platform,
            "platform.outbox_messages", resources).ShouldBeEmpty();
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Outbox,
            "platform.outbox_replay_requests", resources).ShouldBe(new HashSet<string> { "SELECT" }, true);
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Outbox,
            "platform.outbox_recovery_events", resources).ShouldBe(new HashSet<string> { "SELECT", "INSERT" }, true);
        foreach (RuntimeDatabaseRoleKind role in new[] { RuntimeDatabaseRoleKind.Organization, RuntimeDatabaseRoleKind.Identity })
        {
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, "platform.outbox_replay_requests", resources).ShouldBeEmpty();
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, "platform.outbox_recovery_events", resources).ShouldBeEmpty();
        }
    }

    private sealed class CountingTransport : IOutboxTransport
    {
        public int Calls { get; private set; }
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
