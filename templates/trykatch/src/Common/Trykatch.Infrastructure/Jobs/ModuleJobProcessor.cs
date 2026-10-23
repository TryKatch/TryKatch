using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using System.Text;
using System.Diagnostics.Metrics;
using Trykatch.Application.Execution;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Jobs;

internal sealed class ModuleJobWorkerState
{
    private readonly object sync = new();
    private Snapshot snapshot = new(0, 0, 0, null, null);

    internal void Report(long backlog, long retryable, long terminal, DateTimeOffset? oldest, string? failure)
    {
        lock (sync) snapshot = new Snapshot(backlog, retryable, terminal, oldest, failure);
    }

    internal Snapshot Read()
    {
        lock (sync) return snapshot;
    }

    internal sealed record Snapshot(long Backlog, long Retryable, long Terminal, DateTimeOffset? Oldest, string? Failure);
}

internal sealed class ModuleJobHealthCheck(ModuleJobWorkerState state) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ModuleJobWorkerState.Snapshot snapshot = state.Read();
        Dictionary<string, object> data = new()
        {
            ["backlog"] = snapshot.Backlog,
            ["retryable"] = snapshot.Retryable,
            ["terminal"] = snapshot.Terminal
        };
        if (snapshot.Oldest is { } oldest) data["oldestAgeSeconds"] = Math.Max(0, (DateTimeOffset.UtcNow - oldest).TotalSeconds);
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult result = snapshot.Failure is not null
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Module job reconciliation failed.", data: data)
            : snapshot.Terminal > 0 || snapshot.Backlog > 10_000
                || snapshot.Oldest is { } value && DateTimeOffset.UtcNow - value > TimeSpan.FromMinutes(10)
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded("Module job backlog is delayed.", data: data)
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Module job reconciliation is active.", data);
        return Task.FromResult(result);
    }
}

/// <summary>Repairs the work-to-Quartz bridge and materializes committed recurring occurrences.</summary>
internal sealed partial class ModuleJobProcessor(
    IServiceScopeFactory scopeFactory,
    ISchedulerFactory schedulerFactory,
    IConfiguration configuration,
    ModuleJobWorkerState state,
    TimeProvider timeProvider,
    ILogger<ModuleJobProcessor> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Trykatch.ModuleJobs");
    private static readonly Histogram<long> Backlog = Meter.CreateHistogram<long>("trykatch.module_jobs.backlog");
    private static readonly Histogram<double> OldestAge = Meter.CreateHistogram<double>("trykatch.module_jobs.oldest_age", "s");
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private DateTimeOffset? forwardAfterCreatedAt;
    private Guid forwardAfterId;
    internal async Task ExecuteWorkAsync(Guid workId, CancellationToken cancellationToken)
    {
        string connectionString = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator);
        await using ModuleWorkDbContext db = CreateContext(connectionString);
        WorkReference? reference = await db.ModuleWorkItems.AsNoTracking()
            .Where(work => work.Id == workId)
            .Select(work => new WorkReference(work.OperationKey, work.OperationVersion))
            .SingleOrDefaultAsync(cancellationToken);
        if (reference is null) return;

        using IServiceScope scope = scopeFactory.CreateScope();
        IOrganizationWorkExecutor executor = scope.ServiceProvider.GetRequiredService<IOrganizationWorkExecutor>();
        _ = await executor.ExecuteAsync(
            new ModuleExecutionRequest(workId, reference.OperationKey, reference.OperationVersion),
            cancellationToken);
    }

    internal async Task ReconcileTargetAsync(string scheduleKey, string targetKey, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        ModuleScheduleRegistry registry = scope.ServiceProvider.GetRequiredService<ModuleScheduleRegistry>();
        await registry.ReconcileTargetAsync(scheduleKey, targetKey, cancellationToken);
    }

    internal static bool TryParseTargetReference(string? reference, out string scheduleKey, out string targetKey)
    {
        scheduleKey = string.Empty;
        targetKey = string.Empty;
        if (string.IsNullOrEmpty(reference)) return false;
        int separator = reference.IndexOf('/');
        if (separator <= 0 || separator == reference.Length - 1 || reference.IndexOf('/', separator + 1) >= 0) return false;
        scheduleKey = reference[..separator];
        targetKey = reference[(separator + 1)..];
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogFailure(logger, exception.GetType().Name);
                state.Report(0, 0, 0, null, exception.GetType().Name);
            }

            try { await Task.Delay(PollInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        IScheduler scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        using IServiceScope scope = scopeFactory.CreateScope();
        ModuleScheduleRegistry registry = scope.ServiceProvider.GetRequiredService<ModuleScheduleRegistry>();
        await registry.ReconcileAsync(cancellationToken);
        await ReconcileRecurringJobsAsync(scheduler, registry.ResolveRegistrations(), cancellationToken);
        await ForwardCommittedWorkAsync(scheduler, cancellationToken);
        await UpdateHealthStateAsync(cancellationToken);
    }

    private async Task ForwardCommittedWorkAsync(IScheduler scheduler, CancellationToken cancellationToken)
    {
        string connectionString = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator);
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        await using ModuleWorkDbContext db = CreateContext(connectionString);
        IQueryable<ModuleWorkItem> pending = EligibleWorkItems(db, now);
        if (forwardAfterCreatedAt is { } after)
            pending = pending.Where(work => work.CreatedAtUtc > after
                || work.CreatedAtUtc == after && work.Id.CompareTo(forwardAfterId) > 0);
        List<ForwardCandidate> candidates = await pending
            .OrderBy(work => work.CreatedAtUtc)
            .ThenBy(work => work.Id)
            .Take(20)
            .Select(work => new ForwardCandidate(work.Id, work.CreatedAtUtc, work.DueAtUtc, work.NextAttemptAtUtc))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0 && forwardAfterCreatedAt is not null)
        {
            forwardAfterCreatedAt = null;
            forwardAfterId = Guid.Empty;
            candidates = await EligibleWorkItems(db, now)
                .OrderBy(work => work.CreatedAtUtc)
                .ThenBy(work => work.Id)
                .Take(20)
                .Select(work => new ForwardCandidate(work.Id, work.CreatedAtUtc, work.DueAtUtc, work.NextAttemptAtUtc))
                .ToListAsync(cancellationToken);
        }

        foreach (ForwardCandidate candidate in candidates)
        {
            JobKey jobKey = WorkJobKey(candidate.WorkId);
            IJobDetail? job = await scheduler.GetJobDetail(jobKey, cancellationToken);
            ITrigger? existing = await scheduler.GetTrigger(WorkTriggerKey(candidate.WorkId), cancellationToken);
            DateTimeOffset startAt = Later(now, candidate.DueAtUtc, candidate.NextAttemptAtUtc);
            if (existing is not null)
            {
                // Keep pending immediate callbacks stable; repair future triggers when the ledger changes readiness.
                if (existing.StartTimeUtc > now && (existing.StartTimeUtc - startAt).Duration() > TimeSpan.FromMilliseconds(1))
                {
                    ITrigger replacement = existing.GetTriggerBuilder().StartAt(startAt).Build();
                    await scheduler.RescheduleJob(existing.Key, replacement, cancellationToken);
                }
                continue;
            }
            job ??= JobBuilder.Create<ModuleWorkBridgeJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData(ModuleQuartzKeys.WorkId, candidate.WorkId.ToString("N"))
                    .RequestRecovery(true)
                    .Build();
            ITrigger trigger = TriggerBuilder.Create()
                .WithIdentity(WorkTriggerKey(candidate.WorkId))
                .ForJob(job)
                .StartAt(startAt)
                .Build();
            try
            {
                if (await scheduler.Exists(jobKey, cancellationToken))
                    await scheduler.ScheduleJob(trigger, default, cancellationToken);
                else
                    await scheduler.ScheduleJob(job, trigger, default, cancellationToken);
            }
            catch (ObjectAlreadyExistsException) { }
        }
        if (candidates.Count > 0)
        {
            ForwardCandidate last = candidates[^1];
            forwardAfterCreatedAt = last.CreatedAtUtc;
            forwardAfterId = last.WorkId;
        }
    }

    private async Task ReconcileRecurringJobsAsync(
        IScheduler scheduler,
        IReadOnlyList<ModuleRecurringScheduleRegistration> registrations,
        CancellationToken cancellationToken)
    {
        HashSet<JobKey> expected = [];
        foreach (ModuleRecurringScheduleRegistration registration in registrations)
        {
            JobKey jobKey = RecurringJobKey(registration.Schedule.ScheduleKey, registration.Target.TargetKey, registration.Target.OrganizationId);
            expected.Add(jobKey);
            TriggerKey triggerKey = RecurringTriggerKey(registration.Schedule.ScheduleKey, registration.Target.TargetKey, registration.Target.OrganizationId);
            if (await scheduler.GetTrigger(triggerKey, cancellationToken) is not null)
            {
                await scheduler.ResumeJob(jobKey, cancellationToken);
                continue;
            }
            IJobDetail job = JobBuilder.Create<ModuleRecurringControlJob>()
                .WithIdentity(jobKey)
                .UsingJobData(ModuleQuartzKeys.TargetReference, registration.Schedule.ScheduleKey + "/" + registration.Target.TargetKey)
                .Build();
            ITrigger trigger = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .ForJob(job)
                .StartAt(registration.Target.StartAtUtc)
                .WithCronSchedule(registration.Schedule.UtcSchedule, builder => builder
                    .InTimeZone(TimeZoneInfo.Utc)
                    .WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
                .Build();
            try { await scheduler.ScheduleJob(job, trigger, default, cancellationToken); }
            catch (ObjectAlreadyExistsException) { }
        }

        await using ModuleWorkDbContext db = CreateContext(RuntimeDatabaseConnectionContract.Get(
            configuration, RuntimeDatabaseConnectionContract.WorkCoordinator));
        var retained = await db.Set<ModuleScheduleCursor>().AsNoTracking()
            .Select(cursor => new { cursor.ScheduleKey, cursor.TargetKey, cursor.OrganizationId }).ToArrayAsync(cancellationToken);
        foreach (var cursor in retained)
        {
            JobKey key = RecurringJobKey(cursor.ScheduleKey, cursor.TargetKey, cursor.OrganizationId);
            if (!expected.Contains(key)) await scheduler.PauseJob(key, cancellationToken);
        }
    }

    private async Task UpdateHealthStateAsync(CancellationToken cancellationToken)
    {
        string connectionString = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator);
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        await using ModuleWorkDbContext db = CreateContext(connectionString);
        IQueryable<ModuleWorkItem> pending = db.ModuleWorkItems.AsNoTracking()
            .Where(work => (work.State == "queued" || work.State == "paused")
                && !work.OperatorPaused
                && (work.DueAtUtc == null || work.DueAtUtc <= now)
                && (work.NextAttemptAtUtc == null || work.NextAttemptAtUtc <= now));
        long backlog = await pending.LongCountAsync(cancellationToken);
        long retryable = await pending.LongCountAsync(work => work.Attempts > 0, cancellationToken);
        long terminal = await db.ModuleWorkItems.AsNoTracking().LongCountAsync(work => work.State == "terminal", cancellationToken);
        DateTimeOffset? oldest = await pending.Select(work => (DateTimeOffset?)work.CreatedAtUtc).MinAsync(cancellationToken);
        state.Report(backlog, retryable, terminal, oldest, null);
        Backlog.Record(backlog, new KeyValuePair<string, object?>("state", "pending"));
        Backlog.Record(retryable, new KeyValuePair<string, object?>("state", "retrying"));
        Backlog.Record(terminal, new KeyValuePair<string, object?>("state", "terminal"));
        OldestAge.Record(oldest is { } instant ? Math.Max(0, (now - instant).TotalSeconds) : 0);
    }

    private static ModuleWorkDbContext CreateContext(string connectionString)
    {
        DbContextOptions<ModuleWorkDbContext> options = new DbContextOptionsBuilder<ModuleWorkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ModuleWorkDbContext(options);
    }

    private static IQueryable<ModuleWorkItem> EligibleWorkItems(ModuleWorkDbContext db, DateTimeOffset now) =>
        db.ModuleWorkItems.AsNoTracking()
            .Where(work => (work.State == "queued" || work.State == "paused"
                    || work.State == "leased" && (work.LeaseUntilUtc == null || work.LeaseUntilUtc <= now))
                && !work.OperatorPaused
                && (work.LastFailureCode == null || work.LastFailureCode != ModuleScheduleRegistry.MissingRegistrationFailureCode));

    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset? dueAtUtc, DateTimeOffset? nextAttemptAtUtc)
    {
        DateTimeOffset result = now;
        if (dueAtUtc is { } due && due > result) result = due;
        if (nextAttemptAtUtc is { } next && next > result) result = next;
        return result;
    }

    private static JobKey WorkJobKey(Guid workId) => new("work-" + workId.ToString("N"), ModuleQuartzKeys.WorkGroup);
    private static TriggerKey WorkTriggerKey(Guid workId) => new("work-trigger-" + workId.ToString("N"), ModuleQuartzKeys.WorkGroup);
    private static JobKey RecurringJobKey(string scheduleKey, string targetKey, Guid organizationId) =>
        new("recurring-" + ReferenceHash(scheduleKey, targetKey, organizationId), ModuleQuartzKeys.RecurringGroup);
    private static TriggerKey RecurringTriggerKey(string scheduleKey, string targetKey, Guid organizationId) =>
        new("recurring-trigger-" + ReferenceHash(scheduleKey, targetKey, organizationId), ModuleQuartzKeys.RecurringGroup);

    private static string ReferenceHash(string scheduleKey, string targetKey, Guid organizationId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('/', scheduleKey, targetKey, organizationId.ToString("N")))));

    [LoggerMessage(EventId = 4501, Level = LogLevel.Error, Message = "Module job reconciliation failed ({FailureType}).")]
    private static partial void LogFailure(ILogger logger, string failureType);

    private sealed record WorkReference(string OperationKey, int OperationVersion);
    private sealed record ForwardCandidate(
        Guid WorkId,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? DueAtUtc,
        DateTimeOffset? NextAttemptAtUtc);
}
