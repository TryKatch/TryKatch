using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class EmailOperatorPermissionTests
{
    [TestMethod]
    public void BuiltInRolesDoNotReceiveEmailReadAndAdministratorMustHoldItExplicitly()
    {
        PlatformPermissions.All.ShouldContain(PlatformPermissions.EmailRead);
        PlatformRoles.All.ShouldAllBe(role => !role.Permissions.Contains(PlatformPermissions.EmailRead));
        EffectivePlatformAccess administrator = new(true, true, PlatformRoles.Administrator, PlatformRoles.Find(PlatformRoles.Administrator)!.Permissions);
        administrator.HasPermission(PlatformPermissions.EmailRead).ShouldBeFalse();
        administrator.HasPermission(PlatformPermissions.UsersManage).ShouldBeTrue();
        administrator.GrantBoundary.ShouldContain(PlatformPermissions.EmailRead);
        EffectivePlatformAccess explicitlyGranted = administrator with
        {
            Permissions = new HashSet<string>(administrator.Permissions, StringComparer.Ordinal) { PlatformPermissions.EmailRead }
        };
        explicitlyGranted.HasPermission(PlatformPermissions.EmailRead).ShouldBeTrue();
        (explicitlyGranted with { IsActive = false }).HasPermission(PlatformPermissions.EmailRead).ShouldBeFalse();
        (explicitlyGranted with { IsActive = false }).GrantBoundary.ShouldBeEmpty();
    }

    [TestMethod]
    public void CustomRolesCanUseAndDelegateOnlyTheirHeldEmailPermission()
    {
        EffectivePlatformAccess reader = new(true, false, "platform-custom-email",
            new HashSet<string>(StringComparer.Ordinal) { PlatformPermissions.EmailRead });
        reader.HasPermission(PlatformPermissions.EmailRead).ShouldBeTrue();
        reader.HasPermission(PlatformPermissions.UsersManage).ShouldBeFalse();
        reader.GrantBoundary.ShouldBe(reader.Permissions);
        (reader with { Permissions = new HashSet<string>(StringComparer.Ordinal) }).HasPermission(PlatformPermissions.EmailRead).ShouldBeFalse();
        (reader with { IsActive = false }).GrantBoundary.ShouldBeEmpty();
    }

    [TestMethod]
    public void DeliveryQueriesHaveClosedBoundariesFiltersAndPagination()
    {
        EmailDeliveryQuery query = EmailDeliveryQuery.Create("organization", int.MaxValue, int.MaxValue, "suppressed", Guid.CreateVersion7())!;
        query.Page.ShouldBe(10_000);
        query.PageSize.ShouldBe(100);
        EmailDeliveryQuery.Create("identity", -1, -1, null)!.PageSize.ShouldBe(1);
        EmailDeliveryQuery.Create("identity", 1, 25, null, Guid.CreateVersion7()).ShouldBeNull();
        EmailDeliveryQuery.Create("identity", 1, 25, null, deliveryId: Guid.Empty).ShouldBeNull();
        EmailDeliveryQuery.Create("organization", 1, 25, "private-error").ShouldBeNull();
        EmailDeliveryQuery.Create("other", 1, 25, null).ShouldBeNull();
    }

    [TestMethod]
    public void DeliveryMetadataNeverEchoesUnknownTemplateStateOrFailureText()
    {
        EmailDeliveryView view = EmailDeliveryMetadataRules.View(Guid.CreateVersion7(), EmailDeliveryBoundary.Identity, null,
            "private-template", 4, false, "private-state", 99, DateTimeOffset.UtcNow, null, null, "private-exception", 999);
        view.TemplateKey.ShouldBe("unavailable");
        view.TemplateVersion.ShouldBeNull();
        view.State.ShouldBe("unknown");
        view.Attempts.ShouldBe(EmailDeliveryPolicy.MaximumAttempts);
        view.FailureCode.ShouldBe("unclassified");
        view.SmtpStatusCode.ShouldBeNull();
        EmailDeliveryMetadataRules.SafeFailure("1").ShouldBe("unclassified");
        EmailDeliveryMetadataRules.SafeFailure(nameof(EmailFailureCode.None)).ShouldBeNull();
        foreach (EmailFailureCode failure in Enum.GetValues<EmailFailureCode>().Where(value => value != EmailFailureCode.None))
            EmailDeliveryMetadataRules.SafeFailure(failure.ToString()).ShouldBe(failure.ToString());
    }
}
