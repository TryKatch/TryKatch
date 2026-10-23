using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Quartz;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Jobs;

internal sealed record ModuleScheduleOccurrenceBatch(
    IReadOnlyList<DateTimeOffset> Occurrences,
    long SkippedOccurrences);

internal sealed record ModuleRecurringScheduleRegistration(
    ModuleRecurringScheduleDescriptor Schedule,
    ModuleJobRegistration Job,
    ModuleRecurringTarget Target,
    string ConfigurationHash);

/// <summary>Resolves explicit recurring targets and atomically advances durable occurrence cursors.</summary>
internal sealed class ModuleScheduleRegistry
{
    private const int MaximumOccurrenceScan = 100_000;
    internal const string MissingRegistrationFailureCode = "schedule_registration_missing";

    private readonly ModuleCatalog catalog;
    private readonly ModuleJobRegistry jobs;
    private readonly IConfiguration configuration;
    private readonly TimeProvider timeProvider;
    private readonly ModuleRecurringTarget[] targets;

    public ModuleScheduleRegistry(
        ModuleCatalog catalog,
        ModuleJobRegistry jobs,
        IEnumerable<ModuleRecurringTarget> targets,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.targets = (targets ?? throw new ArgumentNullException(nameof(targets))).ToArray();
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        EnsureUniqueTargets(this.targets);
    }

    internal IReadOnlyList<ModuleRecurringScheduleRegistration> ResolveRegistrations()
    {
        List<ModuleRecurringScheduleRegistration> result = [];
        foreach (ModuleRecurringTarget target in targets)
        {
            ModuleRecurringScheduleDescriptor? schedule = catalog.Descriptors
                .SelectMany(module => module.RecurringSchedules)
                .SingleOrDefault(item => item.ScheduleKey == target.ScheduleKey);
            if (schedule is null) continue;
            if (!jobs.TryGet(schedule.JobKey, schedule.JobVersion, out ModuleJobRegistration? job)
                || job is null
                || job.Descriptor.ModuleId != schedule.OwnerModuleId
                || job.Descriptor.ExecutionKind != ModuleExecutionKind.ScheduledSystem
                || job.Operation.Descriptor.ExecutionKind != ModuleExecutionKind.ScheduledSystem
                || job.Operation.Descriptor.SystemGrantKey != schedule.SystemGrantKey
                || job.Operation.SystemGrant is not { } grant
                || grant.OwnerModuleId != schedule.OwnerModuleId
                || !grant.AllowedOperationKeys.Contains(job.Descriptor.OperationKey, StringComparer.Ordinal)
                || !grant.AllowedResourceKeys.Contains(job.Operation.Descriptor.ResourcePolicyKey, StringComparer.Ordinal))
                throw new InvalidOperationException($"Recurring schedule '{schedule.ScheduleKey}' does not resolve to its installed scheduled-system grant.");
            if (!CronExpression.TryParse(schedule.UtcSchedule, out _))
                throw new InvalidOperationException($"Recurring schedule '{schedule.ScheduleKey}' has an invalid Quartz expression.");
            if (schedule.MisfirePolicy == ModuleMisfirePolicy.CatchUp
                && (target.MaxCatchUpOccurrences is not (>= 1 and <= 100)
                    || target.MaxCatchUpAge is not { } age || age <= TimeSpan.Zero || age > TimeSpan.FromDays(31)))
                throw new InvalidOperationException($"Catch-up schedule '{schedule.ScheduleKey}' requires occurrence and age bounds.");
            if (schedule.MisfirePolicy == ModuleMisfirePolicy.Coalesce
                && (target.MaxCatchUpOccurrences is not null || target.MaxCatchUpAge is not null))
                throw new InvalidOperationException($"Coalesced schedule '{schedule.ScheduleKey}' cannot set catch-up bounds.");

            string hash = ConfigurationHash(schedule, job, target);
            result.Add(new ModuleRecurringScheduleRegistration(schedule, job, target, hash));
        }
        return result
            .OrderBy(registration => registration.Schedule.ScheduleKey, StringComparer.Ordinal)
            .ThenBy(registration => registration.Target.TargetKey, StringComparer.Ordinal)
            .ThenBy(registration => registration.Target.OrganizationId)
            .ToArray();
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
        => await ReconcileAsync(null, null, cancellationToken);

    internal async Task ReconcileTargetAsync(string scheduleKey, string targetKey, CancellationToken cancellationToken)
        => await ReconcileAsync(scheduleKey, targetKey, cancellationToken);

    private async Task ReconcileAsync(string? requestedScheduleKey, string? requestedTargetKey, CancellationToken cancellationToken)
    {
        if ((requestedScheduleKey is null) != (requestedTargetKey is null))
            throw new ArgumentException("A recurring target reference requires both keys.");
        string connectionString = RuntimeDatabaseConnectionContract.Get(
            configuration,
            RuntimeDatabaseConnectionContract.WorkCoordinator);
        DbContextOptions<ModuleWorkDbContext> options = new DbContextOptionsBuilder<ModuleWorkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        IReadOnlyList<ModuleRecurringScheduleRegistration> registrations = ResolveRegistrations();
        if (requestedScheduleKey is not null)
            registrations = registrations.Where(registration =>
                registration.Schedule.ScheduleKey == requestedScheduleKey
                && registration.Target.TargetKey == requestedTargetKey).ToArray();
        HashSet<(string ScheduleKey, string TargetKey, Guid OrganizationId)> activeKeys = registrations
            .Select(registration => (registration.Schedule.ScheduleKey, registration.Target.TargetKey, registration.Target.OrganizationId))
            .ToHashSet();
        DateTimeOffset horizon = timeProvider.GetUtcNow().ToUniversalTime();

        await using ModuleWorkDbContext db = new(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (ModuleRecurringScheduleRegistration registration in registrations)
            await ReconcileRegistrationAsync(db, registration, horizon, cancellationToken);
        if (requestedScheduleKey is null)
            await PauseMissingTargetsAsync(db, activeKeys, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static ModuleScheduleOccurrenceBatch BuildOccurrenceBatch(
        string utcSchedule,
        ModuleMisfirePolicy policy,
        DateTimeOffset exclusiveCursorUtc,
        DateTimeOffset horizonUtc,
        int? maxCatchUpOccurrences,
        TimeSpan? maxCatchUpAge)
    {
        if (exclusiveCursorUtc.Offset != TimeSpan.Zero || horizonUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Schedule cursors and horizons must be UTC.");
        if (horizonUtc <= exclusiveCursorUtc)
            return new ModuleScheduleOccurrenceBatch([], 0);
        if (!CronExpression.TryParse(utcSchedule, out _))
            throw new ArgumentException("A valid Quartz cron expression is required.", nameof(utcSchedule));

        int? catchUpLimit = null;
        DateTimeOffset ageThreshold = DateTimeOffset.MinValue;
        if (policy == ModuleMisfirePolicy.Coalesce)
        {
            if (maxCatchUpOccurrences is not null || maxCatchUpAge is not null)
                throw new ArgumentException("Coalesce does not accept catch-up bounds.");
        }
        else if (policy == ModuleMisfirePolicy.CatchUp)
        {
            if (maxCatchUpOccurrences is not (>= 1 and <= 100))
                throw new ArgumentOutOfRangeException(nameof(maxCatchUpOccurrences));
            if (maxCatchUpAge is not { } age || age <= TimeSpan.Zero || age > TimeSpan.FromDays(31))
                throw new ArgumentOutOfRangeException(nameof(maxCatchUpAge));
            catchUpLimit = maxCatchUpOccurrences;
            ageThreshold = horizonUtc - age;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        CronExpression cron = new(utcSchedule, TimeZoneInfo.Utc);
        List<DateTimeOffset> occurrences = [];
        DateTimeOffset cursor = exclusiveCursorUtc;
        long count = 0;
        while (true)
        {
            DateTimeOffset? next = cron.GetNextValidTimeAfter(cursor);
            if (next is null || next.Value.ToUniversalTime() > horizonUtc) break;
            if (++count > MaximumOccurrenceScan)
                throw new InvalidOperationException("Recurring schedule exceeds the bounded occurrence scan; its cursor was not advanced.");
            DateTimeOffset instant = next.Value.ToUniversalTime();
            if (policy == ModuleMisfirePolicy.Coalesce)
            {
                if (occurrences.Count == 0) occurrences.Add(instant);
                else occurrences[0] = instant;
            }
            else if (instant > ageThreshold)
            {
                occurrences.Add(instant);
                if (occurrences.Count > catchUpLimit)
                    occurrences.RemoveAt(0);
            }
            cursor = instant;
        }

        return new ModuleScheduleOccurrenceBatch(occurrences, count - occurrences.Count);
    }

    private static async Task ReconcileRegistrationAsync(
        ModuleWorkDbContext db,
        ModuleRecurringScheduleRegistration registration,
        DateTimeOffset horizon,
        CancellationToken cancellationToken)
    {
        ModuleRecurringTarget target = registration.Target;
        string lockKey = string.Join('/', "module-schedule", registration.Schedule.ScheduleKey,
            target.TargetKey, target.OrganizationId.ToString("N"));
        _ = await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        ModuleScheduleCursor? cursor = await db.Set<ModuleScheduleCursor>()
            .SingleOrDefaultAsync(row => row.ScheduleKey == registration.Schedule.ScheduleKey
                && row.TargetKey == target.TargetKey && row.OrganizationId == target.OrganizationId,
                cancellationToken);
        if (cursor is null)
        {
            cursor = new ModuleScheduleCursor
            {
                ScheduleKey = registration.Schedule.ScheduleKey,
                TargetKey = target.TargetKey,
                OrganizationId = target.OrganizationId,
                ConfigurationHash = registration.ConfigurationHash,
                StartAtUtc = target.StartAtUtc,
                ReconciledThroughUtc = target.StartAtUtc.AddTicks(-TimeSpan.TicksPerMicrosecond)
            };
            db.Set<ModuleScheduleCursor>().Add(cursor);
        }
        else if (cursor.ConfigurationHash != registration.ConfigurationHash || cursor.StartAtUtc != target.StartAtUtc)
        {
            throw new InvalidOperationException($"Persisted recurring target '{registration.Schedule.ScheduleKey}/{target.TargetKey}' has incompatible configuration.");
        }

        await db.Set<ModuleWorkItem>()
            .Where(work => work.OrganizationId == target.OrganizationId
                && work.ScheduleKey == registration.Schedule.ScheduleKey
                && work.ScheduleTargetKey == target.TargetKey
                && work.State == "paused" && work.LastFailureCode == MissingRegistrationFailureCode)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(work => work.State, "queued")
                .SetProperty(work => work.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(work => work.LastFailureCode, (string?)null), cancellationToken);

        if (horizon <= cursor.ReconciledThroughUtc) return;
        ModuleScheduleOccurrenceBatch batch = BuildOccurrenceBatch(
            registration.Schedule.UtcSchedule,
            registration.Schedule.MisfirePolicy,
            cursor.ReconciledThroughUtc,
            horizon,
            target.MaxCatchUpOccurrences,
            target.MaxCatchUpAge);

        ModuleSystemGrantDescriptor grant = registration.Job.Operation.SystemGrant!;
        foreach (DateTimeOffset occurrence in batch.Occurrences)
        {
            bool exists = await db.Set<ModuleWorkItem>().AnyAsync(work =>
                work.OrganizationId == target.OrganizationId
                && work.ScheduleKey == registration.Schedule.ScheduleKey
                && work.ScheduleTargetKey == target.TargetKey
                && work.OccurrenceAtUtc == occurrence, cancellationToken);
            if (exists) continue;

            string requestScope = string.Join('/', "recurring", registration.Schedule.ScheduleKey,
                target.TargetKey, target.OrganizationId.ToString("N"), occurrence.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            Guid requestId = DeterministicGuid(requestScope);
            ModuleExecutionIdentity identity = new(
                target.OrganizationId,
                grant.AuditActorId,
                null,
                null,
                ModuleExecutionKind.ScheduledSystem,
                grant.GrantKey,
                grant.GrantVersion,
                null,
                ModuleOperationRegistry.CatalogVersion,
                null);
            ModuleWorkItem work = ModuleJobIntent.CreateWork(
                registration.Job,
                target.OrganizationId,
                identity,
                requestId,
                "schedule/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestScope))),
                target.Payload.GetRawText(),
                "schedule",
                occurrence,
                horizon,
                scheduleKey: registration.Schedule.ScheduleKey,
                scheduleTargetKey: target.TargetKey,
                occurrenceAtUtc: occurrence);
            db.Set<ModuleWorkItem>().Add(work);
        }

        cursor.SkippedOccurrences = checked(cursor.SkippedOccurrences + batch.SkippedOccurrences);
        cursor.ReconciledThroughUtc = horizon;
    }

    private static async Task PauseMissingTargetsAsync(
        ModuleWorkDbContext db,
        HashSet<(string ScheduleKey, string TargetKey, Guid OrganizationId)> activeKeys,
        CancellationToken cancellationToken)
    {
        List<ModuleScheduleCursor> cursors = await db.Set<ModuleScheduleCursor>().ToListAsync(cancellationToken);
        foreach (ModuleScheduleCursor cursor in cursors)
        {
            if (activeKeys.Contains((cursor.ScheduleKey, cursor.TargetKey, cursor.OrganizationId))) continue;
            await db.Set<ModuleWorkItem>()
                .Where(work => work.OrganizationId == cursor.OrganizationId
                    && work.ScheduleKey == cursor.ScheduleKey && work.ScheduleTargetKey == cursor.TargetKey
                    && (work.State == "queued" || work.State == "paused"))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(work => work.State, "paused")
                    .SetProperty(work => work.NextAttemptAtUtc, DateTimeOffset.MaxValue)
                    .SetProperty(work => work.LastFailureCode, MissingRegistrationFailureCode), cancellationToken);
        }
    }

    private static string ConfigurationHash(
        ModuleRecurringScheduleDescriptor schedule,
        ModuleJobRegistration job,
        ModuleRecurringTarget target)
    {
        string material = JsonSerializer.Serialize(new
        {
            schedule.OwnerModuleId,
            schedule.JobKey,
            schedule.UtcSchedule,
            schedule.MisfirePolicy,
            schedule.SystemGrantKey,
            job.Descriptor.OperationKey,
            job.Descriptor.PayloadSchemaKey,
            job.Descriptor.PayloadSchemaVersion,
            target.OrganizationId,
            PayloadHash = OutboxPayloadHash.Compute(target.Payload.GetRawText()),
            target.StartAtUtc,
            target.MaxCatchUpOccurrences,
            target.MaxCatchUpAge
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static Guid DeterministicGuid(string scope) => new(SHA256.HashData(Encoding.UTF8.GetBytes(scope)).AsSpan(0, 16));

    private static void EnsureUniqueTargets(IEnumerable<ModuleRecurringTarget> items)
    {
        HashSet<(string ScheduleKey, string TargetKey)> seen = [];
        foreach (ModuleRecurringTarget target in items)
            if (!seen.Add((target.ScheduleKey, target.TargetKey)))
                throw new InvalidOperationException($"Duplicate recurring target '{target.ScheduleKey}/{target.TargetKey}'.");
    }
}
