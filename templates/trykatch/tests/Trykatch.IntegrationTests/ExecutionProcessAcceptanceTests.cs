using Npgsql;
using Shouldly;
using Trykatch.ExecutionAcceptance;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ExecutionProcessAcceptanceTests
{
    [TestMethod]
    public async Task InstalledProjectsEventAndUserJobSurviveCompetingWorkersAndOsProcessRestart()
    {
        await using ExecutionProcessAcceptanceFixture fixture = await ExecutionProcessAcceptanceFixture.StartAsync();
        AcceptedProjectsWork projectWork = await fixture.SeedProjectsCreateWorkAsync();
        Guid jobTarget = await fixture.SeedSyntheticTargetAsync(projectWork.OrganizationId, projectWork.ActorId);
        Guid deniedTarget = await fixture.SeedSyntheticTargetAsync(projectWork.OrganizationId, projectWork.ActorId);

        ProcessResult produced = await fixture.RunProducerAsync(projectWork, jobTarget);
        produced.Exit.ShouldBe(0, produced.Output + produced.Error);
        produced.Output.ShouldContain("producer-committed");
        ProducerEvidence producer = ExecutionProcessAcceptanceFixture.ReadProducerEvidence(produced.Output);
        (await fixture.CountEffectsAsync(projectWork.OrganizationId)).ShouldBe(0,
            "the child producer must exit after commit before any hosted worker delivers the event or job");
        SourceEvidence source = await fixture.ReadProjectSourceAsync(projectWork.OrganizationId);
        source.LegacyCount.ShouldBe(1);
        source.TypedCount.ShouldBe(1);
        source.Context.OperationKey.ShouldBe("projects.create");
        source.Context.ExecutionKind.ShouldBe(nameof(ModuleExecutionKind.UserCommand));
        source.Payload.OrganizationId.ShouldBe(projectWork.OrganizationId);
        source.Payload.ActorId.ShouldBe(projectWork.ActorId);
        (await fixture.CountProjectsRealtimeHistoryAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(0,
            "the realtime consumer must not append history before a worker handles the committed source event");

        await using NpgsqlConnection effectGate = await fixture.HoldEffectTableAsync();
        await using WorkerProcess workerA = await fixture.StartWorkerAsync("worker-a");
        await using WorkerProcess workerB = await fixture.StartWorkerAsync("worker-b");
        workerA.Ready.ProcessId.ShouldNotBe(workerB.Ready.ProcessId);
        workerA.Ready.SchedulerInstanceId.ShouldNotBe(workerB.Ready.SchedulerInstanceId);
        workerA.Ready.OrganizationRole.ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
        workerA.Ready.OutboxRole.ShouldBe(PostgresRuntimeRoleFixture.OutboxRole);
        workerA.Ready.WorkCoordinatorRole.ShouldBe(PostgresRuntimeRoleFixture.WorkCoordinatorRole);
        workerB.Ready.OrganizationRole.ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
        workerB.Ready.OutboxRole.ShouldBe(PostgresRuntimeRoleFixture.OutboxRole);
        workerB.Ready.WorkCoordinatorRole.ShouldBe(PostgresRuntimeRoleFixture.WorkCoordinatorRole);
        await fixture.WaitForClusterMembersAsync(workerA.Ready.SchedulerInstanceId, workerB.Ready.SchedulerInstanceId);
        await workerA.EnsureRunningAsync();
        await workerB.EnsureRunningAsync();
        await effectGate.CloseAsync();
        await fixture.WaitForCompletionAsync(source.MessageId, projectWork.OrganizationId, jobTarget, producer.JobWorkId);

        (await fixture.CountEffectsAsync(projectWork.OrganizationId)).ShouldBe(2);
        (await fixture.CountEventInboxAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(1);
        (await fixture.CountEventDeliveriesAsync(source.MessageId)).ShouldBe(1);
        (await fixture.CountProjectsRealtimeInboxAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(1);
        (await fixture.CountProjectsRealtimeDeliveryAsync(source.MessageId)).ShouldBe(1);
        ProjectRealtimeEvidence realtime = await fixture.ReadProjectsRealtimeHistoryAsync(projectWork.OrganizationId, source.MessageId);
        realtime.EventId.ShouldBe(source.MessageId);
        realtime.SourceMessageId.ShouldBe(source.MessageId);
        realtime.ModuleId.ShouldBe("projects");
        realtime.EventKey.ShouldBe("projects.created");
        realtime.SchemaVersion.ShouldBe(1);
        realtime.PolicyOwnerModuleId.ShouldBe("projects");
        realtime.PolicyKey.ShouldBe("projects.read");
        realtime.PolicyVersion.ShouldBe(1);
        realtime.AudienceKind.ShouldBe((int)ModuleRealtimeAudienceKind.Organization);
        realtime.UserId.ShouldBeNull();
        realtime.ResourceKey.ShouldBe("projects");
        realtime.ResourceId.ShouldBe(source.Payload.ProjectId);
        (await fixture.ReadEventEffectAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(
            new EventEffectEvidence(source.Payload.ProjectId, AcceptanceModule.AuditActorId, projectWork.ActorId));
        (await fixture.ReadJobEffectAsync(projectWork.OrganizationId, jobTarget)).ShouldBe(
            new JobEffectEvidence(projectWork.ActorId, projectWork.ActorId, true));
        (await fixture.ReadCompletionAuditAsync(projectWork.OrganizationId, producer.JobWorkId, projectWork.ActorId))
            .ShouldContain("UserCommand");
        Guid deliveryId = await fixture.ReadDeliveryIdAsync(source.MessageId);
        (await fixture.ReadEventAuditAsync(projectWork.OrganizationId, deliveryId, AcceptanceModule.AuditActorId,
            projectWork.ActorId)).ShouldContain(AcceptanceModule.EventGrant);

        // This separate negative control is admitted as deferred user work. Its future due time
        // keeps both live workers from racing the parent membership revocation.
        ModuleWorkReceipt revoked = await fixture.ScheduleSyntheticJobAsync(projectWork.OrganizationId,
            projectWork.ActorId, deniedTarget);
        revoked.State.ShouldBe(ModuleWorkState.Scheduled);
        (await fixture.RevokeMembershipAsync(projectWork.OrganizationId, projectWork.ActorId)).ShouldBe(1);
        (await fixture.MakeWorkEligibleAsync(revoked.WorkId)).ShouldBe(1);
        await fixture.WaitForWorkStateAsync(revoked.WorkId, "denied");
        (await fixture.ReadJobEffectAsync(projectWork.OrganizationId, deniedTarget))
            .ShouldBe(new JobEffectEvidence(null, null, false));

        await fixture.AssertRuntimeIsolationAsync(projectWork.OrganizationId, projectWork.ActorId);
        await fixture.AssertInstalledResourceAndRoleSafetyAsync();

        await workerA.StopAsync();
        await workerB.StopAsync();

        // Model a committed effect whose acknowledgement was lost while both workers were
        // stopped. E2/E3 exercise these same durable row transitions in-process; this process
        // fixture proves recovery through a fresh OS worker and existing inbox/completion rows.
        await fixture.SimulateLostAcknowledgementsAsync(source.MessageId, producer.JobWorkId);
        await using WorkerProcess restarted = await fixture.StartWorkerAsync("worker-after-restart");
        await fixture.WaitForAcknowledgementRecoveryAsync(projectWork.OrganizationId, source.MessageId, producer.JobWorkId);
        (await fixture.CountEffectsAsync(projectWork.OrganizationId)).ShouldBe(2);
        (await fixture.CountEventInboxAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(1);
        (await fixture.CountEventDeliveriesAsync(source.MessageId)).ShouldBe(1);
        (await fixture.CountProjectsRealtimeInboxAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(1);
        (await fixture.CountProjectsRealtimeDeliveryAsync(source.MessageId)).ShouldBe(1);
        (await fixture.CountProjectsRealtimeHistoryAsync(projectWork.OrganizationId, source.MessageId)).ShouldBe(1);
        (await fixture.ReadJobEffectAsync(projectWork.OrganizationId, jobTarget))
            .ShouldBe(new JobEffectEvidence(projectWork.ActorId, projectWork.ActorId, true));
    }
}
