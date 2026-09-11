using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Trykatch.Infrastructure.Persistence;

internal sealed record OutboxBatchResult(int Published, int Failed, int Terminalized, int Replayed, long Pending);

internal sealed class OutboxWorkerState
{
    private readonly object gate = new();
    private Task pendingTransport = Task.CompletedTask;
    private OutboxWorkerSnapshot snapshot = new(0, 0, null, null, false, false, null);

    public OutboxWorkerSnapshot Read() { lock (gate) return snapshot; }
    public void Observe(long pending, long terminal, DateTimeOffset? oldest, DateTimeOffset at)
    { lock (gate) snapshot = snapshot with { Pending = pending, Terminal = terminal, Oldest = oldest, ObservedAt = at, DatabaseFailureType = null }; }
    public void DatabaseFailure(string type, bool permanent, DateTimeOffset at)
    { lock (gate) snapshot = snapshot with { DatabaseFailureType = type, PermanentFailure = permanent, ObservedAt = at }; }
    public void TransportBlocked(bool blocked, DateTimeOffset at)
    { lock (gate) snapshot = snapshot with { TransportInvocationBlocked = blocked, ObservedAt = at }; }
    public void TrackTransport(Task observation) { lock (gate) pendingTransport = observation; }
    public Task WaitForTransportAsync(CancellationToken cancellationToken)
    {
        lock (gate) return pendingTransport.WaitAsync(cancellationToken);
    }
}

internal sealed record OutboxWorkerSnapshot(long Pending, long Terminal, DateTimeOffset? Oldest,
    string? DatabaseFailureType, bool PermanentFailure, bool TransportInvocationBlocked, DateTimeOffset? ObservedAt);

internal sealed class OutboxHealthCheck(OutboxWorkerState state, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        OutboxWorkerSnapshot value = state.Read();
        Dictionary<string, object> data = new()
        {
            ["pendingCount"] = value.Pending,
            ["terminalCount"] = value.Terminal,
            ["oldestAgeSeconds"] = value.Oldest is { } oldest ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds) : 0
        };
        if (value.DatabaseFailureType is not null || value.TransportInvocationBlocked)
            return Task.FromResult(HealthCheckResult.Unhealthy("Outbox dispatch is unavailable.", data: data));
        if (value.Terminal > 0)
            return Task.FromResult(HealthCheckResult.Degraded("Outbox has terminal recovery items.", data: data));
        return Task.FromResult(HealthCheckResult.Healthy("Outbox dispatch is available.", data));
    }
}

internal sealed class OutboxBatchProcessor(
    OutboxDbContext dbContext,
    OutboxDelivery delivery,
    IOptions<OutboxRecoveryOptions> options,
    OutboxWorkerState state,
    TimeProvider timeProvider)
{
    private const int MaximumPayloadBytes = 1_048_576;
    private static readonly Meter Meter = new("Trykatch.Outbox");
    private static readonly Histogram<double> BatchDuration = Meter.CreateHistogram<double>("trykatch.outbox.batch.duration", "s");
    private static readonly Histogram<double> LockDuration = Meter.CreateHistogram<double>("trykatch.outbox.lock.duration", "s");
    private static readonly Counter<long> RecoveryCounter = Meter.CreateCounter<long>("trykatch.outbox.recovery");

    public async Task<OutboxBatchResult> ProcessAsync(CancellationToken stoppingToken)
    {
        OutboxRecoveryOptions value = options.Value;
        if (dbContext.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Outbox publication requires a non-retrying database execution strategy.");

        long batchStarted = Stopwatch.GetTimestamp();
        int published = 0, failed = 0, terminalized = 0, replayed = 0;
        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, stoppingToken);
        long lockStarted = Stopwatch.GetTimestamp();
        dbContext.Database.SetCommandTimeout(value.DatabaseCommandTimeout);
        string commandTimeout = $"{Math.Ceiling(value.DatabaseCommandTimeout.TotalMilliseconds)}ms";
        string lockTimeout = $"{Math.Ceiling(value.DatabaseLockTimeout.TotalMilliseconds)}ms";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('statement_timeout', {commandTimeout}, true), set_config('lock_timeout', {lockTimeout}, true), set_config('app.actor_id', '', true), set_config('app.organization_id', '', true)",
            stoppingToken);

        await ObserveBacklogAsync(stoppingToken);

        replayed += await ApplyReplayRequestsAsync(value.BatchSize, stoppingToken);
        terminalized += await TerminalizeByPolicyAsync(value.MaximumAttempts, value.BatchSize, stoppingToken);
        terminalized += await TerminalizeOversizeAsync(value.BatchSize, stoppingToken);

        DateTimeOffset deadline = timeProvider.GetUtcNow() + value.BatchWorkBudget;
        OutboxMessage[] messages = await dbContext.Messages.FromSqlInterpolated($"""
            SELECT * FROM platform.outbox_messages
            WHERE "ProcessedAt" IS NULL AND "ExhaustedAt" IS NULL
              AND "Attempts" < {value.MaximumAttempts}
              AND octet_length("Payload"::text) <= {MaximumPayloadBytes}
            ORDER BY "OccurredAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT {value.BatchSize}
            """).ToArrayAsync(stoppingToken);

        foreach (OutboxMessage message in messages)
        {
            TimeSpan remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            bool delivered = await delivery.DeliverAsync(message,
                remaining < value.TransportTimeout ? remaining : value.TransportTimeout,
                stoppingToken);
            if (delivered) published++;
            else
            {
                failed++;
                if (message.Attempts >= value.MaximumAttempts)
                {
                    message.ExhaustedAt = timeProvider.GetUtcNow();
                    dbContext.RecoveryEvents.Add(new OutboxRecoveryEvent
                    {
                        MessageId = message.Id,
                        ReplayGeneration = message.ReplayGeneration,
                        Outcome = "terminal",
                        FailureCode = message.LastErrorCode ?? "attempts_exhausted",
                        FailureType = message.LastErrorType ?? "transport_failure",
                        OccurredAt = message.ExhaustedAt.Value
                    });
                    terminalized++;
                    RecoveryCounter.Add(1, new KeyValuePair<string, object?>("outcome", "terminal"));
                }
            }
            if (state.Read().TransportInvocationBlocked) break;
        }

        await dbContext.SaveChangesAsync(stoppingToken);
        await transaction.CommitAsync(stoppingToken);
        LockDuration.Record(Stopwatch.GetElapsedTime(lockStarted).TotalSeconds);

        await ObserveBacklogAsync(stoppingToken);
        BatchDuration.Record(Stopwatch.GetElapsedTime(batchStarted).TotalSeconds);
        return new(published, failed, terminalized, replayed, state.Read().Pending);
    }

    private async Task ObserveBacklogAsync(CancellationToken cancellationToken)
    {
        long pending = await dbContext.Messages.LongCountAsync(x => x.ProcessedAt == null && x.ExhaustedAt == null, cancellationToken);
        long terminal = await dbContext.Messages.LongCountAsync(x => x.ProcessedAt == null && x.ExhaustedAt != null, cancellationToken);
        DateTimeOffset? oldest = await dbContext.Messages
            .Where(x => x.ProcessedAt == null && x.ExhaustedAt == null)
            .Select(x => (DateTimeOffset?)x.OccurredAt).MinAsync(cancellationToken);
        state.Observe(pending, terminal, oldest, timeProvider.GetUtcNow());
    }

    private async Task<int> ApplyReplayRequestsAsync(int batchSize, CancellationToken cancellationToken)
    {
        OutboxReplayRequest[] requests = await dbContext.ReplayRequests.FromSqlInterpolated($"""
            SELECT request.* FROM platform.outbox_replay_requests request
            WHERE NOT EXISTS (SELECT 1 FROM platform.outbox_recovery_events event WHERE event."RequestId" = request."RequestId")
            ORDER BY request."RequestedAt", request."RequestId"
            LIMIT {batchSize}
            """).AsNoTracking().ToArrayAsync(cancellationToken);
        int replayed = 0;
        foreach (OutboxReplayRequest request in requests)
        {
            string outcome = await ApplyReplayRequestAsync(request, cancellationToken);
            if (outcome == "replayed") replayed++;
            RecoveryCounter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }
        await dbContext.Database.ExecuteSqlRawAsync("SELECT set_config('app.actor_id', '', true)", cancellationToken);
        return replayed;
    }

    private async Task<string> ApplyReplayRequestAsync(OutboxReplayRequest request, CancellationToken cancellationToken)
    {
        // Serialize even a missing target, then use a fresh READ COMMITTED snapshot to
        // re-read the immutable completion marker before any reset can take place.
        string messageKey = request.MessageId.ToString();
        string actor = request.ActorId.ToString();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({messageKey}, 0)), set_config('app.actor_id', {actor}, true)",
            cancellationToken);
        if (await dbContext.RecoveryEvents.AnyAsync(x => x.RequestId == request.RequestId, cancellationToken))
            return "duplicate";

        DbConnection connection = dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            WITH target AS MATERIALIZED (
              SELECT "ProcessedAt", "ExhaustedAt", "ReplayGeneration"
              FROM platform.outbox_messages WHERE "Id" = @message_id FOR UPDATE
            ), reset AS (
              UPDATE platform.outbox_messages message
              SET "Attempts" = 0, "ExhaustedAt" = NULL, "LastErrorCode" = NULL, "LastErrorType" = NULL,
                  "ReplayGeneration" = message."ReplayGeneration" + 1
              WHERE message."Id" = @message_id AND message."ProcessedAt" IS NULL
                AND message."ExhaustedAt" IS NOT NULL AND message."ReplayGeneration" = @expected_generation
              RETURNING message."ReplayGeneration"
            ), classified AS (
              SELECT CASE
                WHEN EXISTS (SELECT 1 FROM reset) THEN 'replayed'
                WHEN NOT EXISTS (SELECT 1 FROM target) THEN 'not_found'
                WHEN (SELECT "ReplayGeneration" FROM target) <> @expected_generation THEN 'stale_generation'
                WHEN (SELECT "ExhaustedAt" FROM target) IS NULL OR (SELECT "ProcessedAt" FROM target) IS NOT NULL THEN 'not_exhausted'
                ELSE 'stale_generation' END AS outcome,
                COALESCE((SELECT "ReplayGeneration" FROM reset), (SELECT "ReplayGeneration" FROM target), @expected_generation) AS generation
            )
            INSERT INTO platform.outbox_recovery_events
              ("Id", "MessageId", "ReplayGeneration", "Outcome", "FailureCode", "FailureType", "OccurredAt", "RequestId", "ActorId")
            SELECT @event_id, @message_id, generation, outcome, NULL, NULL, @occurred_at, @request_id, @actor_id FROM classified
            RETURNING "Outcome"
            """;
        Add(command, "message_id", request.MessageId);
        Add(command, "event_id", Guid.CreateVersion7());
        Add(command, "expected_generation", request.ExpectedFailedGeneration);
        Add(command, "occurred_at", timeProvider.GetUtcNow());
        Add(command, "request_id", request.RequestId);
        Add(command, "actor_id", request.ActorId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string ?? "duplicate";
    }

    private Task<int> TerminalizeByPolicyAsync(int maximumAttempts, int batchSize, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH candidates AS (
              SELECT "Id" FROM platform.outbox_messages
              WHERE "ProcessedAt" IS NULL AND "ExhaustedAt" IS NULL AND "Attempts" >= {maximumAttempts}
              ORDER BY "OccurredAt", "Id" FOR UPDATE SKIP LOCKED LIMIT {batchSize}
            ), terminal AS (
              UPDATE platform.outbox_messages message SET "ExhaustedAt" = {timeProvider.GetUtcNow()},
                "LastErrorCode" = COALESCE(message."LastErrorCode", 'attempts_exhausted'),
                "LastErrorType" = COALESCE(message."LastErrorType", 'transport_failure')
              FROM candidates WHERE message."Id" = candidates."Id"
              RETURNING message.*
            )
            INSERT INTO platform.outbox_recovery_events
              ("Id", "MessageId", "ReplayGeneration", "Outcome", "FailureCode", "FailureType", "OccurredAt")
            SELECT gen_random_uuid(), "Id", "ReplayGeneration", 'terminal', "LastErrorCode", left("LastErrorType", 240), "ExhaustedAt" FROM terminal
            ON CONFLICT DO NOTHING
            """, cancellationToken);

    private Task<int> TerminalizeOversizeAsync(int batchSize, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH candidates AS (
              SELECT "Id" FROM platform.outbox_messages
              WHERE "ProcessedAt" IS NULL AND "ExhaustedAt" IS NULL
                AND octet_length("Payload"::text) > {MaximumPayloadBytes}
              ORDER BY "OccurredAt", "Id" FOR UPDATE SKIP LOCKED LIMIT {batchSize}
            ), terminal AS (
              UPDATE platform.outbox_messages message SET "ExhaustedAt" = {timeProvider.GetUtcNow()},
                "LastErrorCode" = 'payload_too_large', "LastErrorType" = 'payload_guard'
              FROM candidates WHERE message."Id" = candidates."Id"
              RETURNING message.*
            )
            INSERT INTO platform.outbox_recovery_events
              ("Id", "MessageId", "ReplayGeneration", "Outcome", "FailureCode", "FailureType", "OccurredAt")
            SELECT gen_random_uuid(), "Id", "ReplayGeneration", 'terminal', "LastErrorCode", left("LastErrorType", 240), "ExhaustedAt" FROM terminal
            ON CONFLICT DO NOTHING
            """, cancellationToken);

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
