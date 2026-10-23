using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Quartz;
using Shouldly;
using Trykatch.Application;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private const string RecurringScheduleKey = "probe.scheduled-minute";
    private const string RecurringTargetKey = "lifecycle-probe";

    [TestMethod]
    public async Task E4QuartzForwardingPreservesDelayedDueTimeAndRunsImmediateWorkOnce()
    {
        SeededWork immediate = await fixture.SeedUserWorkAsync("probe.command");
        SeededWork delayed = await fixture.SeedUserWorkAsync("probe.command");
        await fixture.TerminalizeOtherWorkAsync(immediate.WorkId, delayed.WorkId);
        DateTimeOffset dueAt = DateTimeOffset.UtcNow.AddMinutes(3);
        Guid requestId = Guid.NewGuid();
        ModuleWorkReceipt delayedReceipt = await fixture.ScheduleAsync(
            delayed, requestId, ExecutionFixture.PayloadFor(delayed), dueAt);
        delayedReceipt.State.ShouldBe(ModuleWorkState.Scheduled);

        using ServiceProvider host = fixture.CreateJobHost();
        IScheduler scheduler = await host.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();
        try
        {
            await host.GetRequiredService<ModuleJobProcessor>().ReconcileAsync(CancellationToken.None);

            TriggerKey delayedTriggerKey = new("work-trigger-" + delayedReceipt.WorkId.ToString("N"), "trykatch-module-work");
            ITrigger? delayedTrigger = await scheduler.GetTrigger(delayedTriggerKey);
            delayedTrigger.ShouldNotBeNull();
            (delayedTrigger!.StartTimeUtc - dueAt).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(2));

            await fixture.WaitForEffectCountAsync(immediate.OrganizationId, 1);
            (await fixture.CountEffectsAsync(immediate.OrganizationId)).ShouldBe(1);
            (await fixture.ReadWorkStateAsync(delayedReceipt.WorkId)).ShouldBe("queued");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task E4HostedWorkersStartExecuteAndStopWithinTheShutdownBudget(bool cancelInFlight)
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command", cancel: cancelInFlight, holdHandler: cancelInFlight);
        await fixture.TerminalizeOtherWorkAsync(work.WorkId);
        using IHost host = fixture.CreateHostedJobHost();
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(20));
        await host.StartAsync(budget.Token);
        try
        {
            if (cancelInFlight)
                await host.Services.GetRequiredService<ProbeExecutionBehavior>().WaitUntilReachedAsync();
            else
                await fixture.WaitForEffectCountAsync(work.OrganizationId, 1);
        }
        finally { await host.StopAsync(budget.Token); }
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(cancelInFlight ? 0 : 1);
        (await fixture.ReadWorkStateAsync(work.WorkId)).ShouldBe(cancelInFlight ? "queued" : "completed");
    }

    [TestMethod]
    public async Task E4DuplicateQuartzCallbacksKeepE2EffectExactlyOnce()
    {
        SeededWork work = await fixture.SeedScheduledWorkAsync();
        using ServiceProvider host = fixture.CreateJobHost();
        ModuleJobProcessor processor = host.GetRequiredService<ModuleJobProcessor>();

        await Task.WhenAll(
            processor.ExecuteWorkAsync(work.WorkId, CancellationToken.None),
            processor.ExecuteWorkAsync(work.WorkId, CancellationToken.None));

        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
        (await fixture.ReadWorkStateAsync(work.WorkId)).ShouldBe("completed");
    }

    [TestMethod]
    public async Task E4QuartzRestartRepairForwardsAnExpiredLease()
    {
        SeededWork work = await fixture.SeedScheduledWorkAsync();
        _ = await fixture.ClaimAsync(work);
        await fixture.ExpireLeaseAsync(work);
        await fixture.TerminalizeOtherWorkAsync(work.WorkId);

        using ServiceProvider host = fixture.CreateJobHost();
        IScheduler scheduler = await host.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();
        try
        {
            await host.GetRequiredService<ModuleJobProcessor>().ReconcileAsync(CancellationToken.None);
            await fixture.WaitForWorkStateAsync(work.WorkId, "completed");
            (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
    }

    [TestMethod]
    public async Task E4RecurringPoliciesApplyBoundsAndConcurrentReconciliationSharesCursorIdentity()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset horizon = start.AddMinutes(10);
        string payload = ExecutionFixture.PayloadFor(seed);

        await using (ServiceProvider coalesce = fixture.CreateRecurringHost(seed, payload, start, horizon,
            ModuleMisfirePolicy.Coalesce))
        {
            await ExecutionFixture.ReconcileSchedulesAsync(coalesce);
            (IReadOnlyList<DateTimeOffset> occurrences, long skipped, DateTimeOffset reconciledThrough) =
                await fixture.ReadScheduleStateAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId);
            occurrences.ShouldBe([horizon]);
            skipped.ShouldBe(10);
            reconciledThrough.ShouldBe(horizon);
        }

        await fixture.ClearRecurringStateAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId);
        await using ServiceProvider catchUp = fixture.CreateRecurringHost(seed, payload, start, horizon,
            ModuleMisfirePolicy.CatchUp, maxCatchUpOccurrences: 2, maxCatchUpAge: TimeSpan.FromMinutes(5));
        await Task.WhenAll(
            ExecutionFixture.ReconcileSchedulesAsync(catchUp),
            ExecutionFixture.ReconcileSchedulesAsync(catchUp));

        (IReadOnlyList<DateTimeOffset> catchUpOccurrences, long catchUpSkipped, DateTimeOffset catchUpThrough) =
            await fixture.ReadScheduleStateAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId);
        catchUpOccurrences.ShouldBe([start.AddMinutes(9), horizon]);
        catchUpSkipped.ShouldBe(9);
        catchUpThrough.ShouldBe(horizon);
        (await fixture.CountRecurringWorkAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task E4RecurringStartAtIsIncludedAsTheFirstNominalOccurrence()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using ServiceProvider host = fixture.CreateRecurringHost(seed, ExecutionFixture.PayloadFor(seed), start, start,
            ModuleMisfirePolicy.Coalesce);

        await ExecutionFixture.ReconcileSchedulesAsync(host);

        (IReadOnlyList<DateTimeOffset> occurrences, long skipped, DateTimeOffset reconciledThrough) =
            await fixture.ReadScheduleStateAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId);
        occurrences.ShouldBe([start]);
        skipped.ShouldBe(0);
        reconciledThrough.ShouldBe(start);
    }

    [TestMethod]
    public async Task E4JobVersionChangeDoesNotDuplicateAnExistingOccurrenceIdentity()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset horizon = start.AddMinutes(1);
        string payload = ExecutionFixture.PayloadFor(seed);

        await using (ServiceProvider versionOne = fixture.CreateRecurringHost(seed, payload, start, horizon,
            ModuleMisfirePolicy.Coalesce, scheduledJobVersion: 1))
            await ExecutionFixture.ReconcileSchedulesAsync(versionOne);

        await fixture.RewindScheduleCursorAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId, start);
        await using ServiceProvider versionTwo = fixture.CreateRecurringHost(seed, payload, start, horizon,
            ModuleMisfirePolicy.Coalesce, scheduledJobVersion: 2);
        await ExecutionFixture.ReconcileSchedulesAsync(versionTwo);

        (await fixture.CountRecurringWorkAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task E4MissingRecurringRegistrationPausesWorkAndReenableResumesIt()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstHorizon = start.AddMinutes(2);
        string payload = ExecutionFixture.PayloadFor(seed);

        await using (ServiceProvider enabled = fixture.CreateRecurringHost(seed, payload, start, firstHorizon,
            ModuleMisfirePolicy.Coalesce))
            await ExecutionFixture.ReconcileSchedulesAsync(enabled);

        await using (ServiceProvider disabled = fixture.CreateRecurringHost(seed, payload, start, firstHorizon,
            ModuleMisfirePolicy.Coalesce, registerTarget: false))
            await ExecutionFixture.ReconcileSchedulesAsync(disabled);

        await using (ServiceProvider moduleDisabled = fixture.CreateRecurringHost(seed, payload, start, firstHorizon,
            ModuleMisfirePolicy.Coalesce, includeScheduleModule: false))
            await ExecutionFixture.ReconcileSchedulesAsync(moduleDisabled);

        (await fixture.ReadRecurringWorkStatesAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId))
            .ShouldAllBe(state => state == "paused");

        DateTimeOffset nextHorizon = start.AddMinutes(3);
        await using ServiceProvider reenabled = fixture.CreateRecurringHost(seed, payload, start, nextHorizon,
            ModuleMisfirePolicy.Coalesce);
        await ExecutionFixture.ReconcileSchedulesAsync(reenabled);

        (await fixture.ReadRecurringWorkStatesAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId))
            .ShouldAllBe(state => state == "queued");
        (await fixture.CountRecurringWorkAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task E4QuartzReenableRepairsPreviouslyPausedRecurringWork()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using ServiceProvider enabled = fixture.CreateRecurringHost(seed, ExecutionFixture.PayloadFor(seed),
            start, start.AddMinutes(1), ModuleMisfirePolicy.Coalesce);
        await ExecutionFixture.ReconcileSchedulesAsync(enabled);
        Guid workId = await fixture.FindRecurringWorkAsync(seed.OrganizationId);
        await fixture.TerminalizeOtherWorkAsync(workId);
        IScheduler scheduler = await enabled.GetRequiredService<ISchedulerFactory>().GetScheduler();
        try
        {
            await using ServiceProvider disabled = fixture.CreateRecurringHost(seed, ExecutionFixture.PayloadFor(seed),
                start, start.AddMinutes(1), ModuleMisfirePolicy.Coalesce, registerTarget: false);
            await disabled.GetRequiredService<ModuleJobProcessor>().ReconcileAsync(CancellationToken.None);
            TriggerKey key = new("work-trigger-" + workId.ToString("N"), "trykatch-module-work");
            (await scheduler.GetTrigger(key)).ShouldBeNull();
            await enabled.GetRequiredService<ModuleJobProcessor>().ReconcileAsync(CancellationToken.None);
            (await scheduler.GetTrigger(key)).ShouldNotBeNull();
            await scheduler.Start();
            await fixture.WaitForWorkStateAsync(workId, "completed");
            (await fixture.CountEffectsAsync(seed.OrganizationId)).ShouldBe(1);
        }
        finally { await scheduler.Shutdown(waitForJobsToComplete: true); }
    }

    [TestMethod]
    public async Task E4RemovingTargetStopsAnAlreadyForwardedOccurrence()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string payload = ExecutionFixture.PayloadFor(seed);
        await using (ServiceProvider enabled = fixture.CreateRecurringHost(seed, payload, start, start.AddMinutes(1), ModuleMisfirePolicy.Coalesce))
            await ExecutionFixture.ReconcileSchedulesAsync(enabled);
        Guid workId = await fixture.FindRecurringWorkAsync(seed.OrganizationId);
        await using ServiceProvider disabled = fixture.CreateRecurringHost(seed, payload, start, start.AddMinutes(1),
            ModuleMisfirePolicy.Coalesce, registerTarget: false);
        // A previously persisted Quartz trigger may fire before the next reconciliation pass.
        await disabled.GetRequiredService<ModuleJobProcessor>().ExecuteWorkAsync(workId, CancellationToken.None);
        (await fixture.CountEffectsAsync(seed.OrganizationId)).ShouldBe(0);
        (await fixture.ReadWorkStateAsync(workId)).ShouldBe("paused");
    }

    [TestMethod]
    public async Task E4CursorAndOccurrenceWorkRollBackTogetherWhenOccurrenceInsertFails()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset horizon = start.AddMinutes(1);
        await fixture.CreateScheduleInsertFailureAsync();
        try
        {
            await using ServiceProvider host = fixture.CreateRecurringHost(seed, ExecutionFixture.PayloadFor(seed), start, horizon,
                ModuleMisfirePolicy.Coalesce);
            DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(() => ExecutionFixture.ReconcileSchedulesAsync(host));
            failure.GetBaseException().ShouldBeOfType<PostgresException>().SqlState.ShouldBe("P0001");

            (await fixture.CountScheduleCursorsAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId)).ShouldBe(0);
            (await fixture.CountRecurringWorkAsync(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId)).ShouldBe(0);
        }
        finally
        {
            await fixture.DropScheduleInsertFailureAsync();
        }
    }
}

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private sealed partial class ExecutionFixture
    {
        public IHost CreateHostedJobHost()
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            foreach (ServiceDescriptor descriptor in CreateJobHostServices(configuration))
                builder.Services.Add(descriptor);
            return builder.Build();
        }

        public ServiceProvider CreateJobHost()
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            ServiceCollection host = CreateJobHostServices(configuration);
            return host.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public ServiceProvider CreateRecurringHost(
            SeededWork seed,
            string payload,
            DateTimeOffset start,
            DateTimeOffset horizon,
            ModuleMisfirePolicy policy,
            int? maxCatchUpOccurrences = null,
            TimeSpan? maxCatchUpAge = null,
            bool registerTarget = true,
            bool includeScheduleModule = true,
            int scheduledJobVersion = 1)
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            ServiceCollection host = CreateJobHostServices(configuration, includeScheduleModule, policy, scheduledJobVersion);
            host.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FrozenTimeProvider(horizon)));
            if (registerTarget)
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                host.AddModuleRecurringTarget(RecurringScheduleKey, RecurringTargetKey, seed.OrganizationId,
                    document.RootElement, start, maxCatchUpOccurrences, maxCatchUpAge);
            }
            return host.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        private static ServiceCollection CreateJobHostServices(
            IConfiguration configuration,
            bool includeScheduleModule = true,
            ModuleMisfirePolicy policy = ModuleMisfirePolicy.Coalesce,
            int scheduledJobVersion = 1)
        {
            IModule[] modules = includeScheduleModule
                ? [new ProjectsModule(), new DocumentsModule(), new ScheduledProbeModule(policy, scheduledJobVersion)]
                : [new ProjectsModule(), new DocumentsModule()];
            ServiceCollection host = new();
            host.AddSingleton(configuration);
            host.AddLogging();
            host.AddApplication();
            host.AddModules(configuration, modules);
            host.AddInfrastructure(configuration);
            host.AddSingleton(new ProbeExecutionBehavior());
            return host;
        }

        public static async Task ReconcileSchedulesAsync(ServiceProvider provider)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ModuleScheduleRegistry>().ReconcileAsync(CancellationToken.None);
        }

        public async Task<Guid> FindRecurringWorkAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT \"Id\" FROM infrastructure.module_work_items WHERE \"OrganizationId\"=@organization AND \"ScheduleKey\"=@schedule LIMIT 1", connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("schedule", RecurringScheduleKey);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }

        public Task TerminalizeOtherWorkAsync(params Guid[] keepWorkIds) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"State\"='terminal' WHERE \"State\" IN ('queued','paused','leased') AND \"Id\" <> ALL(@keep)",
            ("keep", keepWorkIds));

        public Task<string> ReadWorkStateAsync(Guid workId) => ReadJsonOwnerAsync(
            "SELECT \"State\" FROM infrastructure.module_work_items WHERE \"Id\"=@work", ("work", workId));

        public async Task WaitForWorkStateAsync(Guid workId, string expected)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            string state;
            do
            {
                state = await ReadWorkStateAsync(workId);
                if (state == expected) return;
                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException($"Expected work {workId} in state {expected}; observed {state}.");
        }

        public async Task WaitForEffectCountAsync(Guid organizationId, long expected)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await CountEffectsAsync(organizationId) >= expected) return;
                await Task.Delay(25);
            }
            throw new TimeoutException($"Expected {expected} probe effects for organization {organizationId}.");
        }

        public async Task<(IReadOnlyList<DateTimeOffset> Occurrences, long Skipped, DateTimeOffset ReconciledThrough)> ReadScheduleStateAsync(
            string scheduleKey, string targetKey, Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT cursor."ReconciledThroughUtc", cursor."SkippedOccurrences",
                       array_agg(work."OccurrenceAtUtc" ORDER BY work."OccurrenceAtUtc") FILTER (WHERE work."OccurrenceAtUtc" IS NOT NULL)
                FROM infrastructure.module_schedule_cursors AS cursor
                LEFT JOIN infrastructure.module_work_items AS work
                  ON work."OrganizationId"=cursor."OrganizationId"
                 AND work."ScheduleKey"=cursor."ScheduleKey"
                 AND work."ScheduleTargetKey"=cursor."TargetKey"
                WHERE cursor."ScheduleKey"=@schedule AND cursor."TargetKey"=@target
                  AND cursor."OrganizationId"=@organization
                GROUP BY cursor."ReconciledThroughUtc", cursor."SkippedOccurrences"
                """, connection);
            command.Parameters.AddWithValue("schedule", scheduleKey);
            command.Parameters.AddWithValue("target", targetKey);
            command.Parameters.AddWithValue("organization", organizationId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("The recurring schedule cursor was not persisted.");
            DateTimeOffset through = reader.GetFieldValue<DateTimeOffset>(0);
            long skipped = reader.GetInt64(1);
            DateTimeOffset[] occurrences = reader.IsDBNull(2) ? [] : reader.GetFieldValue<DateTimeOffset[]>(2);
            return (occurrences, skipped, through);
        }

        public Task<long> CountRecurringWorkAsync(string scheduleKey, string targetKey, Guid organizationId) => CountOwnerAsync("""
            SELECT count(*) FROM infrastructure.module_work_items
            WHERE "ScheduleKey"=@schedule AND "ScheduleTargetKey"=@target AND "OrganizationId"=@organization
            """, ("schedule", scheduleKey), ("target", targetKey), ("organization", organizationId));

        public async Task<string[]> ReadRecurringWorkStatesAsync(string scheduleKey, string targetKey, Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "State" FROM infrastructure.module_work_items
                WHERE "ScheduleKey"=@schedule AND "ScheduleTargetKey"=@target AND "OrganizationId"=@organization
                ORDER BY "OccurrenceAtUtc"
                """, connection);
            command.Parameters.AddWithValue("schedule", scheduleKey);
            command.Parameters.AddWithValue("target", targetKey);
            command.Parameters.AddWithValue("organization", organizationId);
            List<string> states = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) states.Add(reader.GetString(0));
            return [.. states];
        }

        public async Task<long> CountScheduleCursorsAsync(string scheduleKey, string targetKey, Guid organizationId) =>
            await CountOwnerAsync("SELECT count(*) FROM infrastructure.module_schedule_cursors WHERE \"ScheduleKey\"=@schedule AND \"TargetKey\"=@target AND \"OrganizationId\"=@organization",
                ("schedule", scheduleKey), ("target", targetKey), ("organization", organizationId));

        public Task RewindScheduleCursorAsync(string scheduleKey, string targetKey, Guid organizationId, DateTimeOffset through) =>
            ExecuteOwnerAsync("UPDATE infrastructure.module_schedule_cursors SET \"ReconciledThroughUtc\"=@through WHERE \"ScheduleKey\"=@schedule AND \"TargetKey\"=@target AND \"OrganizationId\"=@organization",
                ("through", through), ("schedule", scheduleKey), ("target", targetKey), ("organization", organizationId));

        public Task ClearRecurringStateAsync(string scheduleKey, string targetKey, Guid organizationId) => ExecuteOwnerAsync("""
            DELETE FROM infrastructure.module_work_items WHERE "ScheduleKey"=@schedule AND "ScheduleTargetKey"=@target AND "OrganizationId"=@organization;
            DELETE FROM infrastructure.module_schedule_cursors WHERE "ScheduleKey"=@schedule AND "TargetKey"=@target AND "OrganizationId"=@organization;
            """, ("schedule", scheduleKey), ("target", targetKey), ("organization", organizationId));

        public Task CreateScheduleInsertFailureAsync() => ExecuteOwnerAsync("""
            CREATE FUNCTION infrastructure.fail_lifecycle_schedule_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."ScheduleKey" = 'probe.scheduled-minute' THEN RAISE EXCEPTION 'injected recurring insert failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_lifecycle_schedule_insert BEFORE INSERT ON infrastructure.module_work_items
            FOR EACH ROW EXECUTE FUNCTION infrastructure.fail_lifecycle_schedule_insert();
            """);

        public Task DropScheduleInsertFailureAsync() => ExecuteOwnerAsync("""
            DROP TRIGGER IF EXISTS fail_lifecycle_schedule_insert ON infrastructure.module_work_items;
            DROP FUNCTION IF EXISTS infrastructure.fail_lifecycle_schedule_insert();
            """);

        private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private sealed class ScheduledProbeModule(ModuleMisfirePolicy policy, int scheduledJobVersion) : IModule
        {
            private readonly ProbeModule probe = new();
            public ModuleDescriptor Descriptor { get; } = CreateDescriptor(policy, scheduledJobVersion);

            private static ModuleDescriptor CreateDescriptor(ModuleMisfirePolicy policy, int scheduledJobVersion)
            {
                ModuleDescriptor descriptor = new ProbeModule().Descriptor;
                ModuleJobDescriptor[] jobs = descriptor.Jobs
                    .Select(job => job.JobKey == "probe.scheduled" ? job with { JobVersion = scheduledJobVersion } : job)
                    .ToArray();
                return descriptor with
                {
                    Jobs = jobs,
                    RecurringSchedules =
                    [
                        new ModuleRecurringScheduleDescriptor(RecurringScheduleKey, "probe", "probe.scheduled",
                            scheduledJobVersion, "0 * * * * ?", policy, "probe.scheduled-grant")
                    ]
                };
            }

            public void Register(IServiceCollection services, IConfiguration configuration) => probe.Register(services, configuration);
        }
    }
}
