using Shouldly;
using Trykatch.ExecutionAcceptance;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProjectsRealtimeProcessAcceptanceTests
{
    [TestMethod]
    public async Task RealtimeHistoryAndInboxRollbackTogetherWhenProjectsInboxInsertFails()
    {
        await using ExecutionProcessAcceptanceFixture fixture = await ExecutionProcessAcceptanceFixture.StartAsync();
        AcceptedProjectsWork work = await fixture.SeedProjectsCreateWorkAsync();
        Guid target = await fixture.SeedSyntheticTargetAsync(work.OrganizationId, work.ActorId);

        ProcessResult produced = await fixture.RunProducerAsync(work, target);
        produced.Exit.ShouldBe(0, produced.Output + produced.Error);
        produced.Output.ShouldContain("producer-committed");
        SourceEvidence source = await fixture.ReadProjectSourceAsync(work.OrganizationId);
        source.TypedCount.ShouldBe(1);
        source.LegacyCount.ShouldBe(1);
        (await fixture.CountProjectsRealtimeHistoryAsync(work.OrganizationId, source.MessageId)).ShouldBe(0);

        await fixture.CreateProjectsRealtimeInboxFailureGateAsync();
        await using (WorkerProcess worker = await fixture.StartWorkerAsync("projects-realtime-rollback"))
        {
            await fixture.WaitForProjectsRealtimeDeliveryStateAsync(source.MessageId, "queued", attempted: true);
            await worker.StopAsync();
        }

        (await fixture.CountProjectsRealtimeHistoryAsync(work.OrganizationId, source.MessageId)).ShouldBe(0,
            "the history append must roll back with the failed consumer inbox insert");
        (await fixture.CountProjectsRealtimeInboxAsync(work.OrganizationId, source.MessageId)).ShouldBe(0);

        await fixture.DropProjectsRealtimeInboxFailureGateAsync();
        (await fixture.MakeProjectsRealtimeDeliveryEligibleAsync(source.MessageId)).ShouldBe(1);
        await using WorkerProcess restarted = await fixture.StartWorkerAsync("projects-realtime-after-rollback");
        await fixture.WaitForProjectsRealtimeCompletionAsync(work.OrganizationId, source.MessageId);

        (await fixture.CountProjectsRealtimeHistoryAsync(work.OrganizationId, source.MessageId)).ShouldBe(1);
        (await fixture.CountProjectsRealtimeInboxAsync(work.OrganizationId, source.MessageId)).ShouldBe(1);
        ProjectRealtimeEvidence notification = await fixture.ReadProjectsRealtimeHistoryAsync(work.OrganizationId, source.MessageId);
        notification.EventId.ShouldBe(source.MessageId);
        notification.SourceMessageId.ShouldBe(source.MessageId);
        notification.ResourceKey.ShouldBe("projects");
        notification.ResourceId.ShouldBe(source.Payload.ProjectId);
    }
}
