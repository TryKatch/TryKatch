using Trykatch.ExecutionAcceptance;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class RealtimeProcessAcceptanceTests
{
    [TestMethod]
    public async Task AuthenticatedCookieAndHubSendCrossProductionApiProcessesWithPrefixIsolation()
    {
        await using RealtimeProcessAcceptanceFixture fixture = await RealtimeProcessAcceptanceFixture.StartAsync();
        RealtimeAcceptanceApiProcess first = await fixture.StartApiReplicaAsync("replica-a");
        RealtimeAcceptanceApiProcess second = await fixture.StartApiReplicaAsync("replica-b");
        using CookieApiClient owner = await fixture.CreateOrganizationOwnerAsync(first);
        await RealtimeProcessAcceptanceFixture.AssertCookieWorksOnOtherHostAsync(owner, second);

        HubProbeClient firstClient = fixture.CreateHubClient(first, owner.Cookies);
        HubProbeClient secondClient = fixture.CreateHubClient(second, owner.Cookies);
        await firstClient.StartAsync();
        await secondClient.StartAsync();

        Guid remoteNonce = Guid.NewGuid();
        string remoteOutcome = await RealtimeProcessAcceptanceFixture.SendAndWaitAsync(second, firstClient, remoteNonce, TimeSpan.FromSeconds(10));
        remoteOutcome.ShouldBe("send-completed");
        firstClient.AssertReceived(remoteNonce);

        RealtimeAcceptanceApiProcess separateNamespace = await fixture.StartApiReplicaAsync(
            "prefix-probe", "s2-other-application");
        Guid isolatedNonce = Guid.NewGuid();
        firstClient.Expect(isolatedNonce);
        string isolatedOutcome = await separateNamespace.SendProbeAsync(firstClient.ConnectionId, isolatedNonce,
            TimeSpan.FromSeconds(4));
        isolatedOutcome.ShouldBe("send-completed",
            "the alternate namespace must reach its own Valkey channel so a missing prefix cannot hide behind an outage");
        (await firstClient.WaitForNoReceiptAsync(isolatedNonce, TimeSpan.FromSeconds(2))).ShouldBeTrue(
            "a send from another application namespace must not reach this process's connection");

        first.ProcessId.ShouldNotBe(second.ProcessId);
        secondClient.ConnectionId.ShouldNotBe(firstClient.ConnectionId);
    }

    [TestMethod]
    public async Task BackplaneOutageDoesNotFabricateReceiptAndExistingAndNewClientsRecover()
    {
        await using RealtimeProcessAcceptanceFixture fixture = await RealtimeProcessAcceptanceFixture.StartAsync();
        RealtimeAcceptanceApiProcess first = await fixture.StartApiReplicaAsync("replica-a");
        RealtimeAcceptanceApiProcess second = await fixture.StartApiReplicaAsync("replica-b");
        using CookieApiClient owner = await fixture.CreateOrganizationOwnerAsync(first);
        await RealtimeProcessAcceptanceFixture.AssertCookieWorksOnOtherHostAsync(owner, second);

        HubProbeClient existingClient = fixture.CreateHubClient(first, owner.Cookies);
        await existingClient.StartAsync();
        Guid outageNonce = Guid.NewGuid();
        existingClient.Expect(outageNonce);
        await fixture.StopValkeyAsync();
        string outageOutcome = await second.SendProbeAsync(existingClient.ConnectionId, outageNonce,
            TimeSpan.FromSeconds(5));
        (outageOutcome is "send-failed" or "send-completed").ShouldBeTrue();
        (await existingClient.WaitForNoReceiptAsync(outageNonce, TimeSpan.FromSeconds(2))).ShouldBeTrue(
            "a send completion or exception during outage is not evidence of remote receipt");

        await fixture.RestartValkeyAtSameEndpointAsync();
        Guid existingRecoveryNonce = Guid.NewGuid();
        await RealtimeProcessAcceptanceFixture.SendUntilReceivedAsync(second, existingClient, existingRecoveryNonce, TimeSpan.FromSeconds(20));
        existingClient.AssertReceived(existingRecoveryNonce);

        HubProbeClient newClient = fixture.CreateHubClient(first, owner.Cookies);
        await newClient.StartAsync();
        Guid newRecoveryNonce = Guid.NewGuid();
        await RealtimeProcessAcceptanceFixture.SendUntilReceivedAsync(second, newClient, newRecoveryNonce, TimeSpan.FromSeconds(20));
        newClient.AssertReceived(newRecoveryNonce);

        first.ProcessId.ShouldNotBe(second.ProcessId);
    }
}
