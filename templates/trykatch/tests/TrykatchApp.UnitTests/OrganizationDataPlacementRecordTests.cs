using Shouldly;
using TrykatchApp.Domain.Organizations;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class OrganizationDataPlacementRecordTests
{
    [TestMethod]
    public void FailedPlacementCanRetryAndBecomeReadyWithoutChangingItsIdentity()
    {
        Guid organizationId = Guid.CreateVersion7();
        DateTimeOffset failedAt = DateTimeOffset.UtcNow;
        OrganizationDataPlacementRecord record = OrganizationDataPlacementRecord.Begin(
            organizationId,
            placement: OrganizationDataPlacementKind.Shared,
            provider: "postgres",
            regionOrStamp: "shared-a");

        record.MarkFailed("provisioning_failed", failedAt);
        record.BeginRetry(failedAt.AddMinutes(1));
        record.MarkReady("trykatch", "secret:shared", "1.0.0", failedAt.AddMinutes(2));

        record.OrganizationId.ShouldBe(organizationId);
        record.State.ShouldBe(OrganizationProvisioningState.Ready);
        record.FailureCode.ShouldBeNull();
        record.DatabaseIdentifier.ShouldBe("trykatch");
        record.ReadyAt.ShouldBe(failedAt.AddMinutes(2));
    }

    [TestMethod]
    public void ReadyPlacementCannotBeRestartedAsARetry()
    {
        OrganizationDataPlacementRecord record = OrganizationDataPlacementRecord.Begin(
            Guid.CreateVersion7(),
            placement: OrganizationDataPlacementKind.Shared,
            provider: "postgres",
            regionOrStamp: null);
        record.MarkReady("trykatch", "secret:shared", "1.0.0", DateTimeOffset.UtcNow);

        Should.Throw<InvalidOperationException>(() => record.BeginRetry(DateTimeOffset.UtcNow));
    }
}
