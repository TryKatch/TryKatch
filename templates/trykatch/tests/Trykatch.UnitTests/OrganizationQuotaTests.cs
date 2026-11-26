using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationQuotaTests
{
    [TestMethod]
    public void FiniteEntitlementAcceptsExactlyTheRemainingCapacityIncludingHolds()
    {
        Evaluate(OrganizationEntitlementMode.Finite, limit: 10, committed: 6, held: 2, requested: 2)
            .ShouldBe(new(QuotaReservationDisposition.Reserved));
    }

    [TestMethod]
    public void FiniteZeroLimitRejectsPositiveRequests()
    {
        Evaluate(OrganizationEntitlementMode.Finite, limit: 0, committed: 0, held: 0, requested: 1)
            .ShouldBe(new(QuotaReservationDisposition.Denied, "quota_exhausted"));
    }

    [TestMethod]
    public void FiniteEntitlementDeniesWhenCommittedAndHeldUnitsExceedTheLimit()
    {
        Evaluate(OrganizationEntitlementMode.Finite, limit: 10, committed: 7, held: 2, requested: 2)
            .ShouldBe(new(QuotaReservationDisposition.Denied, "quota_exhausted"));
    }

    [TestMethod]
    public void FiniteDowngradeDeniesWhenExistingUsageAlreadyExceedsTheNewLimit()
    {
        Evaluate(OrganizationEntitlementMode.Finite, limit: 10, committed: 11, held: 2, requested: 1)
            .ShouldBe(new(QuotaReservationDisposition.Denied, "quota_exhausted"));
    }

    [TestMethod]
    public void FiniteEntitlementRequiresCompleteInventory()
    {
        Evaluate(OrganizationEntitlementMode.Finite, limit: 100, inventoryComplete: false)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_inventory_initializing"));
    }

    [TestMethod]
    public void DisabledAndUnlimitedEntitlementsAcceptWithoutAConfiguredCeilingOrCompleteInventory()
    {
        Evaluate(OrganizationEntitlementMode.Disabled, limit: null, inventoryComplete: false)
            .ShouldBe(new(QuotaReservationDisposition.Reserved));
        Evaluate(OrganizationEntitlementMode.Unlimited, limit: null, inventoryComplete: false)
            .ShouldBe(new(QuotaReservationDisposition.Reserved));
    }

    [TestMethod]
    public void UnconfiguredEntitlementIsDistinctFromExplicitUnlimited()
    {
        Evaluate(OrganizationEntitlementMode.Unconfigured, limit: null)
            .ShouldBe(new(QuotaReservationDisposition.Denied, "quota_unconfigured"));
    }

    [TestMethod]
    public void NonpositiveRequestsAreDenied()
    {
        Evaluate(OrganizationEntitlementMode.Disabled, requested: 0)
            .ShouldBe(new(QuotaReservationDisposition.Denied, "quota_request_invalid"));
        Evaluate(OrganizationEntitlementMode.Disabled, requested: -1)
            .ShouldBe(new(QuotaReservationDisposition.Denied, "quota_request_invalid"));
    }

    [TestMethod]
    [DataRow(OrganizationEntitlementMode.Disabled, 1L)]
    [DataRow(OrganizationEntitlementMode.Unlimited, 1L)]
    [DataRow(OrganizationEntitlementMode.Finite, null)]
    [DataRow(OrganizationEntitlementMode.Finite, -1L)]
    public void InvalidEntitlementProjectionIsUnavailable(OrganizationEntitlementMode mode, long? limit)
    {
        Evaluate(mode, limit: limit)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_projection_invalid"));
    }

    [TestMethod]
    public void EmptyProjectionVersionIsUnavailable()
    {
        Evaluate(OrganizationEntitlementMode.Disabled, version: Guid.Empty)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_projection_invalid"));
    }

    [TestMethod]
    public void UnknownEntitlementModeIsUnavailable()
    {
        Evaluate((OrganizationEntitlementMode)999)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_projection_invalid"));
    }

    [TestMethod]
    public void NegativeExistingUsageIsUnavailable()
    {
        Evaluate(OrganizationEntitlementMode.Unlimited, committed: -1)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_usage_invalid"));
        Evaluate(OrganizationEntitlementMode.Disabled, held: -1)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_usage_invalid"));
    }

    [TestMethod]
    [DataRow(OrganizationEntitlementMode.Disabled)]
    [DataRow(OrganizationEntitlementMode.Unlimited)]
    [DataRow(OrganizationEntitlementMode.Finite)]
    public void UsageArithmeticOverflowIsUnavailableForEveryAcceptingMode(OrganizationEntitlementMode mode)
    {
        Evaluate(mode, limit: mode == OrganizationEntitlementMode.Finite ? long.MaxValue : null,
                committed: long.MaxValue, held: 0, requested: 1)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_usage_invalid"));
    }

    [TestMethod]
    public void ExactInt64MaximumUsageIsAccepted()
    {
        Evaluate(OrganizationEntitlementMode.Finite, limit: long.MaxValue,
                committed: long.MaxValue - 1, held: 0, requested: 1)
            .ShouldBe(new(QuotaReservationDisposition.Reserved));
    }

    [TestMethod]
    public void HeldAndRequestedUnitsOverflowIsUnavailable()
    {
        Evaluate(OrganizationEntitlementMode.Unlimited, committed: 0, held: long.MaxValue, requested: 1)
            .ShouldBe(new(QuotaReservationDisposition.Unavailable, "quota_usage_invalid"));
    }

    private static QuotaCapacityDecision Evaluate(
        OrganizationEntitlementMode mode,
        long? limit = null,
        bool inventoryComplete = true,
        long committed = 0,
        long held = 0,
        long requested = 1,
        Guid? version = null)
    {
        OrganizationEntitlement entitlement = new(
            "documents.storage-bytes",
            mode,
            limit,
            version ?? Guid.NewGuid(),
            "configuration",
            DateTimeOffset.UnixEpoch);

        return QuotaCapacityPolicy.Evaluate(entitlement, inventoryComplete, committed, held, requested);
    }
}
