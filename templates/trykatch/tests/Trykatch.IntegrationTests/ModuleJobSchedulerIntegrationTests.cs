using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Execution;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task SubmissionDeduplicatesInsideOneTransactionAndPersistsAfterCommit()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        Guid requestId = Guid.NewGuid();
        string payload = ExecutionFixture.PayloadFor(seed);

        (ModuleWorkReceipt first, ModuleWorkReceipt repeated) = await fixture.SubmitTwiceAsync(seed, requestId, payload);

        first.State.ShouldBe(ModuleWorkState.Accepted);
        repeated.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
        repeated.WorkId.ShouldBe(first.WorkId);
        repeated.AcceptedAtUtc.ShouldBe(first.AcceptedAtUtc);
        (await fixture.CountSubmissionRowsAsync(seed, requestId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task CompetingSubmissionWaitsForTheFirstTransactionAndReadsItsCommittedReceipt()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        Guid requestId = Guid.NewGuid();

        (ModuleWorkReceipt first, ModuleWorkReceipt second) = await fixture.SubmitConcurrentDuplicateAsync(
            seed, requestId, ExecutionFixture.PayloadFor(seed));

        first.State.ShouldBe(ModuleWorkState.Accepted);
        second.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
        second.WorkId.ShouldBe(first.WorkId);
        (await fixture.CountSubmissionRowsAsync(seed, requestId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task SubmissionIntentConflictsDoNotExposeTheAcceptedWorkId()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        Guid requestId = Guid.NewGuid();
        string payload = ExecutionFixture.PayloadFor(seed);
        (await fixture.SubmitAsync(seed, requestId, payload)).State.ShouldBe(ModuleWorkState.Accepted);
        Guid scheduledRequestId = Guid.NewGuid();
        DateTimeOffset dueAt = DateTimeOffset.UtcNow.AddMinutes(5);
        (await fixture.ScheduleAsync(seed, scheduledRequestId, payload, dueAt)).State.ShouldBe(ModuleWorkState.Scheduled);

        ModuleWorkReceipt changedPayload = await fixture.SubmitAsync(seed, requestId, ExecutionFixture.PayloadFor(seed, "changed"));
        ModuleWorkReceipt changedMode = await fixture.ScheduleAsync(seed, requestId, payload,
            DateTimeOffset.UtcNow.AddMinutes(5));
        ModuleWorkReceipt changedDue = await fixture.ScheduleAsync(seed, scheduledRequestId, payload, dueAt.AddMinutes(1));

        changedPayload.State.ShouldBe(ModuleWorkState.Rejected);
        changedPayload.WorkId.ShouldBe(Guid.Empty);
        changedPayload.FailureCode.ShouldBe("request_id_conflict");
        changedMode.State.ShouldBe(ModuleWorkState.Rejected);
        changedMode.WorkId.ShouldBe(Guid.Empty);
        changedMode.FailureCode.ShouldBe("request_id_conflict");
        changedDue.State.ShouldBe(ModuleWorkState.Rejected);
        changedDue.WorkId.ShouldBe(Guid.Empty);
        changedDue.FailureCode.ShouldBe("request_id_conflict");
        (await fixture.CountSubmissionRowsAsync(seed, requestId)).ShouldBe(1);
        (await fixture.CountSubmissionRowsAsync(seed, scheduledRequestId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task DuplicateReceiptIsReauthorizedAgainstCurrentMembership()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        OrganizationAccess previouslyResolved = await fixture.ResolveAccessAsync(seed.OrganizationId, seed.ActorId);
        Guid requestId = Guid.NewGuid();
        string payload = ExecutionFixture.PayloadFor(seed);
        ModuleWorkReceipt accepted = await fixture.SubmitAsync(seed, requestId, payload);
        accepted.State.ShouldBe(ModuleWorkState.Accepted);

        (await fixture.RevokeMembershipAsync(seed.OrganizationId, seed.ActorId)).ShouldBe(1);
        ModuleWorkReceipt afterRevocation = await fixture.SubmitAsync(seed, requestId, payload, previouslyResolved);

        afterRevocation.State.ShouldBe(ModuleWorkState.Rejected);
        afterRevocation.WorkId.ShouldBe(Guid.Empty);
        afterRevocation.FailureCode.ShouldBe("not_authorized");
    }

    [TestMethod]
    public async Task UserCommandSubmissionRejectsSystemContextEvenWhenItsActorHasMembership()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");

        ModuleWorkReceipt receipt = await fixture.SubmitAsSystemAsync(seed, Guid.NewGuid(), ExecutionFixture.PayloadFor(seed));

        receipt.State.ShouldBe(ModuleWorkState.Rejected);
        receipt.WorkId.ShouldBe(Guid.Empty);
        receipt.FailureCode.ShouldBe("not_authorized");
    }

    [TestMethod]
    public async Task RequestIdentityScopesSubmissionReceiptsByOrganizationAndActor()
    {
        SeededWork firstSeed = await fixture.SeedUserWorkAsync("probe.command");
        SeededWork secondSeed = await fixture.SeedUserWorkAsync("probe.command");
        Guid requestId = Guid.NewGuid();

        ModuleWorkReceipt first = await fixture.SubmitAsync(firstSeed, requestId, ExecutionFixture.PayloadFor(firstSeed));
        ModuleWorkReceipt second = await fixture.SubmitAsync(secondSeed, requestId, ExecutionFixture.PayloadFor(secondSeed));

        first.State.ShouldBe(ModuleWorkState.Accepted);
        second.State.ShouldBe(ModuleWorkState.Accepted);
        second.WorkId.ShouldNotBe(first.WorkId);
        (await fixture.CountSubmissionRowsAsync(firstSeed, requestId)).ShouldBe(1);
        (await fixture.CountSubmissionRowsAsync(secondSeed, requestId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task EnqueueAndOuterBusinessWriteRollBackTogether()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        Guid requestId = Guid.NewGuid();

        ModuleWorkReceipt receipt = await fixture.SubmitAndRollBackBusinessWriteAsync(seed, requestId, ExecutionFixture.PayloadFor(seed));

        receipt.State.ShouldBe(ModuleWorkState.Accepted);
        (await fixture.CountSubmissionRowsAsync(seed, requestId)).ShouldBe(0);
        (await fixture.ReadProbeMarkerAsync(seed)).ShouldBe("seed");
    }

    [TestMethod]
    public async Task ScheduledSubmissionWaitsUntilDueThenUsesTheNormalExecutor()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        Guid requestId = Guid.NewGuid();
        DateTimeOffset dueAt = DateTimeOffset.UtcNow.AddMinutes(5);

        ModuleWorkReceipt receipt = await fixture.ScheduleAsync(seed, requestId, ExecutionFixture.PayloadFor(seed), dueAt);

        receipt.State.ShouldBe(ModuleWorkState.Scheduled);
        receipt.WorkId.ShouldNotBe(Guid.Empty);
        (await fixture.ExecuteSubmissionAsync(seed, receipt.WorkId)).State.ShouldBe(ModuleExecutionState.Paused);
        await fixture.MakeSubmissionDueAsync(receipt.WorkId);
        (await fixture.ExecuteSubmissionAsync(seed, receipt.WorkId)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.CountEffectsAsync(seed.OrganizationId)).ShouldBe(1);
    }
}

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private sealed partial class ExecutionFixture
    {
        public static string PayloadFor(SeededWork seed, string marker = "submission-payload") =>
            JsonSerializer.Serialize(new ProbePayload(seed.PayloadRecordId, seed.ActorId, false, false, false, marker, null), WebJson);

        public async Task<ModuleWorkReceipt> SubmitAsync(
            SeededWork seed,
            Guid requestId,
            string payload,
            OrganizationAccess? resolvedAccess = null)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await InitializeSubmissionScopeAsync(scope, seed, resolvedAccess);
            ModuleWorkReceipt receipt = await Scheduler(scope).EnqueueAsync(Request(requestId, payload));
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }

        public async Task<ModuleWorkReceipt> SubmitAsSystemAsync(SeededWork seed, Guid requestId, string payload)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(seed.OrganizationId, seed.ActorId, CancellationToken.None);
            OrganizationContext context = (OrganizationContext)scope.ServiceProvider.GetRequiredService<IOrganizationContext>();
            context.InitializeSystem(new OrganizationSystemAccess(seed.OrganizationId, "work", seed.ActorId,
                new HashSet<string>(StringComparer.Ordinal) { "probe.write" }));
            ModuleWorkReceipt receipt = await Scheduler(scope).EnqueueAsync(Request(requestId, payload));
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }

        public async Task<ModuleWorkReceipt> ScheduleAsync(
            SeededWork seed,
            Guid requestId,
            string payload,
            DateTimeOffset dueAtUtc)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await InitializeSubmissionScopeAsync(scope, seed, null);
            ModuleWorkReceipt receipt = await Scheduler(scope).ScheduleAsync(Request(requestId, payload), dueAtUtc);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }

        public async Task<(ModuleWorkReceipt First, ModuleWorkReceipt Repeated)> SubmitTwiceAsync(
            SeededWork seed,
            Guid requestId,
            string payload)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await InitializeSubmissionScopeAsync(scope, seed, null);
            PostgresModuleJobScheduler scheduler = Scheduler(scope);
            ModuleWorkReceipt first = await scheduler.EnqueueAsync(Request(requestId, payload));
            ModuleWorkReceipt repeated = await scheduler.EnqueueAsync(Request(requestId, payload));
            await transaction.CommitAsync(CancellationToken.None);
            return (first, repeated);
        }

        public async Task<(ModuleWorkReceipt First, ModuleWorkReceipt Second)> SubmitConcurrentDuplicateAsync(
            SeededWork seed,
            Guid requestId,
            string payload)
        {
            await using AsyncServiceScope firstScope = services.CreateAsyncScope();
            await using IOrganizationDataScope firstTransaction = await InitializeSubmissionScopeAsync(firstScope, seed, null);
            ModuleWorkReceipt first = await Scheduler(firstScope).EnqueueAsync(Request(requestId, payload));
            Task<ModuleWorkReceipt> competing = SubmitAsync(seed, requestId, payload);
            await WaitForSubmissionLockWaiterAsync();
            await firstTransaction.CommitAsync(CancellationToken.None);
            ModuleWorkReceipt second = await competing.WaitAsync(TimeSpan.FromSeconds(10));
            return (first, second);
        }

        public async Task<ModuleWorkReceipt> SubmitAndRollBackBusinessWriteAsync(SeededWork seed, Guid requestId, string payload)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await InitializeSubmissionScopeAsync(scope, seed, null);
            ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await OrganizationTransactionEnlistment.EnlistAsync(
                scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>(), application,
                seed.OrganizationId, seed.ActorId, CancellationToken.None);
            const string marker = "changed-in-transaction";
            await application.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE app.probe_records SET \"Marker\"={marker} WHERE \"Id\"={seed.PayloadRecordId}");
            ModuleWorkReceipt receipt = await Scheduler(scope).EnqueueAsync(Request(requestId, payload));
            return receipt;
        }

        public async Task<OrganizationAccess> ResolveAccessAsync(Guid organizationId, Guid actorId)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(organizationId, actorId, CancellationToken.None);
            OrganizationAccess? access = await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
                .ResolveAsync(actorId, organizationId, CancellationToken.None);
            access.ShouldNotBeNull();
            await transaction.CommitAsync(CancellationToken.None);
            return access!;
        }

        public async Task<long> CountSubmissionRowsAsync(SeededWork seed, Guid requestId) => await CountOwnerAsync("""
            SELECT count(*) FROM infrastructure.module_work_items
            WHERE "OrganizationId"=@organization AND "ActorId"=@actor AND "RequestId"=@request
                AND "ExecutionIdentityKey"=@identity AND "JobKey"='probe' AND "JobVersion"=1
            """, ("organization", seed.OrganizationId), ("actor", seed.ActorId), ("request", requestId),
            ("identity", $"user/{seed.ActorId:N}"));

        public async Task<string> ReadProbeMarkerAsync(SeededWork seed) => await ReadJsonOwnerAsync(
            "SELECT \"Marker\" FROM app.probe_records WHERE \"OrganizationId\"=@organization AND \"Id\"=@record",
            ("organization", seed.OrganizationId), ("record", seed.PayloadRecordId));

        public async Task<ModuleExecutionOutcome> ExecuteSubmissionAsync(SeededWork seed, Guid workId)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOrganizationWorkExecutor>().ExecuteAsync(
                new ModuleExecutionRequest(workId, "probe.command", 1), CancellationToken.None);
        }

        public Task MakeSubmissionDueAsync(Guid workId) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"DueAtUtc\"=@due, \"NextAttemptAtUtc\"=NULL WHERE \"Id\"=@work",
            ("due", DateTimeOffset.UtcNow.AddSeconds(-1)), ("work", workId));

        private async Task WaitForSubmissionLockWaiterAsync()
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            do
            {
                await using NpgsqlConnection connection = new(owner);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new("""
                    SELECT count(*) FROM pg_stat_activity
                    WHERE wait_event_type='Lock' AND query ILIKE '%pg_advisory_xact_lock%'
                    """, connection);
                if ((long)(await command.ExecuteScalarAsync())! > 0) return;
                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException("The competing submission never waited on its PostgreSQL advisory lock.");
        }

        private static async Task<IOrganizationDataScope> InitializeSubmissionScopeAsync(
            AsyncServiceScope scope,
            SeededWork seed,
            OrganizationAccess? resolvedAccess)
        {
            IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(seed.OrganizationId, seed.ActorId, CancellationToken.None);
            OrganizationAccess access = resolvedAccess ?? (await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
                .ResolveAsync(seed.ActorId, seed.OrganizationId, CancellationToken.None))!;
            scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
            return transaction;
        }

        private static PostgresModuleJobScheduler Scheduler(AsyncServiceScope scope)
        {
            ModuleCatalog catalog = scope.ServiceProvider.GetRequiredService<ModuleCatalog>();
            ModuleOperationRegistry operations = scope.ServiceProvider.GetRequiredService<ModuleOperationRegistry>();
            return new PostgresModuleJobScheduler(new ModuleJobRegistry(catalog, operations),
                scope.ServiceProvider.GetRequiredService<IOrganizationContext>(),
                scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>());
        }

        private static ModuleJobRequest Request(Guid requestId, string payload)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return new ModuleJobRequest(requestId, "probe", 1, document.RootElement.Clone());
        }
    }
}
