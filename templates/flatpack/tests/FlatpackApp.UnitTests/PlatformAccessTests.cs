using FlatpackApp.Application.Identity;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class PlatformAccessTests
{
    [TestMethod]
    public void EveryPlatformRoleUsesKnownPermissions()
    {
        PlatformRoles.All.Select(role => role.Key).Distinct(StringComparer.Ordinal).Count().ShouldBe(PlatformRoles.All.Count);
        PlatformRoles.All.ShouldAllBe(role => role.Permissions.Count > 0);
        PlatformRoles.All.ShouldAllBe(role => role.IsSystem);
        PlatformRoles.All.SelectMany(role => role.Permissions).ShouldAllBe(permission => PlatformPermissions.All.Contains(permission));
        PlatformPermissions.Modules.SelectMany(module => module.Permissions).Select(permission => permission.Key)
            .ShouldBe(PlatformPermissions.All, ignoreOrder: true);
    }

    [TestMethod]
    public void OperatorCannotManagePrivilegedPlatformAccess()
    {
        PlatformRoleDefinition role = PlatformRoles.Find(PlatformRoles.Operator).ShouldNotBeNull();

        role.Permissions.ShouldContain(PlatformPermissions.TenantsManage);
        role.Permissions.ShouldContain(PlatformPermissions.InvitationsManage);
        role.Permissions.ShouldNotContain(PlatformPermissions.UsersManage);
        role.Permissions.ShouldNotContain(PlatformPermissions.AuthenticationManage);
    }

    [TestMethod]
    public void AuditorIsReadOnly()
    {
        PlatformRoleDefinition role = PlatformRoles.Find(PlatformRoles.Auditor).ShouldNotBeNull();

        role.Permissions.ShouldAllBe(permission => permission.EndsWith(".read", StringComparison.Ordinal));
    }
}
