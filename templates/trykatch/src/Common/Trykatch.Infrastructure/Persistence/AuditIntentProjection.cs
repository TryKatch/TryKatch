using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trykatch.Application.Auditing;
using Trykatch.Domain.Organizations;

namespace Trykatch.Infrastructure.Persistence;

public sealed class AuditProjectionOptions
{
    public const string SectionName = "AuditProjection";
    public int BatchSize { get; init; } = 50;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public long BacklogWarningCount { get; init; } = 1_000;
    public TimeSpan BacklogWarningAge { get; init; } = TimeSpan.FromMinutes(2);
}

internal sealed class AuditProjectionDbContext(DbContextOptions<AuditProjectionDbContext> options) : DbContext(options)
{
    public DbSet<AuditIntent> Intents => Set<AuditIntent>();
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditIntent>(entity =>
        {
            entity.ToTable("audit_intents", "platform", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Operation).HasMaxLength(120);
            entity.Property(x => x.SubjectType).HasMaxLength(120);
            entity.Property(x => x.SubjectId).HasMaxLength(160);
            entity.Property(x => x.SubjectDisplayName).HasMaxLength(240);
            entity.Property(x => x.Details).HasColumnType("jsonb").HasMaxLength(2048);
        });
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries", "platform", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.SubjectType).HasMaxLength(120);
            entity.Property(x => x.SubjectId).HasMaxLength(160);
            entity.Property(x => x.SubjectDisplayName).HasMaxLength(240);
            entity.Property(x => x.Details).HasColumnType("jsonb");
        });
    }
}

internal sealed class PostgresAuditIntentProjectionStore(AuditProjectionDbContext dbContext) : IAuditIntentProjectionStore
{
    public async Task<AuditProjectionBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        IQueryable<AuditIntent> pending = dbContext.Intents.AsNoTracking()
            .Where(intent => !dbContext.Entries.Any(entry => entry.Id == intent.Id));
        long count = await pending.LongCountAsync(cancellationToken);
        DateTimeOffset? oldest = await pending.Select(intent => (DateTimeOffset?)intent.OccurredAt)
            .MinAsync(cancellationToken);
        return new(count, oldest);
    }

    public async Task<IReadOnlyList<AuditIntent>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken) =>
        await dbContext.Intents.AsNoTracking()
            .Where(intent => !dbContext.Entries.Any(entry => entry.Id == intent.Id))
            .OrderBy(intent => intent.OccurredAt)
            .ThenBy(intent => intent.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

    public async Task<bool> ProjectAsync(AuditIntent intent, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        string organization = intent.OrganizationId.ToString();
        string actor = intent.ActorId.ToString();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organization}, true), set_config('app.actor_id', {actor}, true)",
            cancellationToken);
        int inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.audit_entries
                ("Id", "OrganizationId", "ActorId", "Action", "SubjectType", "SubjectId", "SubjectDisplayName", "Details", "OccurredAt")
            VALUES
                ({intent.Id}, {intent.OrganizationId}, {intent.ActorId}, {intent.Operation}, {intent.SubjectType},
                 {intent.SubjectId}, {intent.SubjectDisplayName}, {intent.Details}::jsonb, {intent.OccurredAt})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1;
    }
}

internal sealed class AuditProjectionBacklogState
{
    private readonly object gate = new();
    private AuditProjectionSnapshot snapshot = new(0, null, null, null);

    public AuditProjectionSnapshot Read()
    {
        lock (gate) return snapshot;
    }

    public void Success(long count, DateTimeOffset? oldest, DateTimeOffset observedAt)
    {
        lock (gate) snapshot = new(count, oldest, observedAt, null);
    }

    public void Failure(string exceptionType, DateTimeOffset observedAt)
    {
        lock (gate) snapshot = snapshot with { ObservedAt = observedAt, FailureType = exceptionType };
    }
}

internal sealed record AuditProjectionSnapshot(
    long Count,
    DateTimeOffset? OldestOccurredAt,
    DateTimeOffset? ObservedAt,
    string? FailureType);

internal sealed class AuditProjectionCycle(
    AuditProjectionBacklogState backlog,
    TimeProvider timeProvider)
{
    public async Task<AuditProjectionBatch> ExecuteAsync(
        IAuditIntentProjectionStore store,
        int batchSize,
        Action<AuditProjectionSnapshot> observeBacklog,
        CancellationToken cancellationToken)
    {
        try
        {
            AuditProjectionBacklog pending = await store.ReadBacklogAsync(cancellationToken);
            DateTimeOffset observedAt = timeProvider.GetUtcNow();
            backlog.Success(pending.Count, pending.OldestOccurredAt, observedAt);
            AuditProjectionSnapshot snapshot = backlog.Read();
            observeBacklog(snapshot);
            AuditProjectionBatch result = await new AuditIntentProjector(store)
                .ProjectBatchAsync(batchSize, cancellationToken);
            AuditProjectionBacklog remaining = await store.ReadBacklogAsync(cancellationToken);
            backlog.Success(remaining.Count, remaining.OldestOccurredAt, timeProvider.GetUtcNow());
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            backlog.Failure(exception.GetType().FullName ?? exception.GetType().Name, timeProvider.GetUtcNow());
            throw;
        }
    }
}

internal sealed class AuditProjectionHealthCheck(
    AuditProjectionBacklogState state,
    IOptions<AuditProjectionOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        AuditProjectionSnapshot snapshot = state.Read();
        if (snapshot.FailureType is not null)
            return Task.FromResult(HealthCheckResult.Unhealthy("Audit projection polling failed.", data: new Dictionary<string, object> { ["failureType"] = snapshot.FailureType }));
        TimeSpan age = snapshot.OldestOccurredAt is null ? TimeSpan.Zero : timeProvider.GetUtcNow() - snapshot.OldestOccurredAt.Value;
        if (snapshot.Count >= options.Value.BacklogWarningCount || age >= options.Value.BacklogWarningAge)
            return Task.FromResult(HealthCheckResult.Degraded("Audit projection backlog exceeded its warning threshold.", data: new Dictionary<string, object> { ["count"] = snapshot.Count, ["oldestAgeSeconds"] = age.TotalSeconds }));
        return Task.FromResult(HealthCheckResult.Healthy("Audit projection backlog is within bounds."));
    }
}

internal sealed partial class AuditIntentProjectionWorker(
    IConfiguration configuration,
    IOptions<AuditProjectionOptions> options,
    AuditProjectionBacklogState backlog,
    TimeProvider timeProvider,
    ILogger<AuditIntentProjectionWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Trykatch.AuditProjection");
    private static readonly Counter<long> ProjectionCounter = Meter.CreateCounter<long>("trykatch.audit.projections");
    private static readonly Histogram<double> ProjectionLag = Meter.CreateHistogram<double>("trykatch.audit.projection.lag", "s");
    private readonly string connectionString = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Outbox);
    private readonly ObservableGauge<long> backlogCountGauge = Meter.CreateObservableGauge(
        "trykatch.audit.projection.backlog.count", () => backlog.Read().Count);
    private readonly ObservableGauge<double> backlogAgeGauge = Meter.CreateObservableGauge(
        "trykatch.audit.projection.backlog.age",
        () => backlog.Read().OldestOccurredAt is DateTimeOffset oldest
            ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds)
            : 0,
        "s");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                string type = exception.GetType().FullName ?? exception.GetType().Name;
                backlog.Failure(type, timeProvider.GetUtcNow());
                ProjectionCounter.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
                LogProjectionFailure(logger, type);
            }

            await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        DbContextOptions<AuditProjectionDbContext> dbOptions = new DbContextOptionsBuilder<AuditProjectionDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using AuditProjectionDbContext dbContext = new(dbOptions);
        PostgresAuditIntentProjectionStore store = new(dbContext);
        AuditProjectionCycle cycle = new(backlog, timeProvider);
        AuditProjectionBatch result = await cycle.ExecuteAsync(
            store,
            options.Value.BatchSize,
            snapshot =>
            {
                if (snapshot.OldestOccurredAt is DateTimeOffset oldest)
                    ProjectionLag.Record(Math.Max(0, (snapshot.ObservedAt!.Value - oldest).TotalSeconds));
            },
            cancellationToken);
        ProjectionCounter.Add(result.Projected, new KeyValuePair<string, object?>("outcome", "success"));
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Error, Message = "Audit intent projection failed with {ExceptionType}")]
    private static partial void LogProjectionFailure(ILogger logger, string exceptionType);
}
