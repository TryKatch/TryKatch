using Shouldly;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Organizations;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationExecutionPrincipalTests
{
    [TestMethod]
    public void SystemPrincipalHasFinitePermissionsAndNoMembership()
    {
        var permissions = new HashSet<string> { "probe.write" };
        var context = new OrganizationContext();
        Guid org = Guid.NewGuid(), actor = Guid.NewGuid();
        context.InitializeSystem(new OrganizationSystemAccess(org, "test", actor, permissions));
        permissions.Add("organizations.manage");

        context.IsResolved.ShouldBeTrue();
        context.OrganizationId.ShouldBe(org);
        context.ActorId.ShouldBe(actor);
        context.OrganizationSlug.ShouldBe("test");
        context.Permissions.ShouldBe(["probe.write"]);
        Should.Throw<OrganizationMembershipRequiredException>(() => context.MembershipId);
        Should.Throw<InvalidOperationException>(() => context.Initialize(
            new OrganizationAccess(org, "test", actor, Guid.NewGuid(), permissions)));
    }

    [TestMethod]
    public void UserPrincipalCannotBeReplacedWithSystemAuthority()
    {
        var context = new OrganizationContext();
        Guid membership = Guid.NewGuid(), org = Guid.NewGuid(), actor = Guid.NewGuid();
        context.Initialize(new OrganizationAccess(org, "test", actor, membership, new HashSet<string>()));
        context.MembershipId.ShouldBe(membership);
        Should.Throw<InvalidOperationException>(() => context.InitializeSystem(
            new OrganizationSystemAccess(org, "test", actor, new HashSet<string>())));
    }
}
