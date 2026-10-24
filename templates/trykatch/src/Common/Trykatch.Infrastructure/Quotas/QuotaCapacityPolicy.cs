using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed record QuotaCapacityDecision(
    QuotaReservationDisposition Disposition,
    string? FailureCode = null);

internal static class QuotaCapacityPolicy
{
    internal static QuotaCapacityDecision Evaluate(
        OrganizationEntitlement entitlement,
        bool inventoryComplete,
        long committedUnits,
        long heldUnits,
        long requestedUnits)
    {
        if (requestedUnits <= 0)
            return new(QuotaReservationDisposition.Denied, "quota_request_invalid");

        if (committedUnits < 0 || heldUnits < 0)
            return UsageInvalid();

        if (entitlement.Version == Guid.Empty || !HasValidProjection(entitlement))
            return ProjectionInvalid();

        if (entitlement.Mode == OrganizationEntitlementMode.Unconfigured)
            return new(QuotaReservationDisposition.Denied, "quota_unconfigured");

        if (entitlement.Mode == OrganizationEntitlementMode.Finite && !inventoryComplete)
            return new(QuotaReservationDisposition.Unavailable, "quota_inventory_initializing");

        long totalUnits;
        try
        {
            totalUnits = checked(checked(committedUnits + heldUnits) + requestedUnits);
        }
        catch (OverflowException)
        {
            return UsageInvalid();
        }

        if (entitlement.Mode == OrganizationEntitlementMode.Finite && totalUnits > entitlement.Limit!.Value)
            return new(QuotaReservationDisposition.Denied, "quota_exhausted");

        return new(QuotaReservationDisposition.Reserved);
    }

    private static bool HasValidProjection(OrganizationEntitlement entitlement) => entitlement.Mode switch
    {
        OrganizationEntitlementMode.Disabled or OrganizationEntitlementMode.Unlimited => entitlement.Limit is null,
        OrganizationEntitlementMode.Finite => entitlement.Limit is >= 0,
        OrganizationEntitlementMode.Unconfigured => entitlement.Limit is null,
        _ => false
    };

    private static QuotaCapacityDecision ProjectionInvalid() =>
        new(QuotaReservationDisposition.Unavailable, "quota_projection_invalid");

    private static QuotaCapacityDecision UsageInvalid() =>
        new(QuotaReservationDisposition.Unavailable, "quota_usage_invalid");
}
