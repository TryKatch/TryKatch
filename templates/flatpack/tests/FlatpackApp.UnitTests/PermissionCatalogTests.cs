using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Application.Projects;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class PermissionCatalogTests
{
    [TestMethod]
    public void BuiltInCatalogPublishesUniqueMetadataForEveryPermission()
    {
        PermissionCatalog catalog = CreateCatalog();

        catalog.Keys.SetEquals(Permissions.All).ShouldBeTrue();
        catalog.Modules.SelectMany(module => module.Permissions).All(permission =>
            !string.IsNullOrWhiteSpace(permission.Name) && !string.IsNullOrWhiteSpace(permission.Description)).ShouldBeTrue();
    }

    [TestMethod]
    public void CatalogFailsFastOnDuplicatePermissionKeys()
    {
        IPermissionDefinitionProvider duplicateProvider = new TestProvider([
            new("one", "One", "First module", 1, [new("things.read", "Read things", "Read things.")]),
            new("two", "Two", "Second module", 2, [new("things.read", "Read things again", "Duplicate key.")])
        ]);

        Should.Throw<InvalidOperationException>(() => new PermissionCatalog([duplicateProvider]))
            .Message.ShouldContain("Duplicate permission key");
    }

    [TestMethod]
    public void ModulePermissionsContributeDefaultRoleGrants()
    {
        PermissionCatalog catalog = CreateCatalog();

        catalog.GetDefaultsForRole(DefaultOrganizationRoles.Member)
            .ShouldBe([Permissions.MembersRead, Permissions.RolesRead, Permissions.ProjectsRead, Permissions.ProjectsManage], ignoreOrder: true);
        catalog.GetDefaultsForRole(DefaultOrganizationRoles.Viewer)
            .ShouldBe([Permissions.MembersRead, Permissions.RolesRead, Permissions.ProjectsRead], ignoreOrder: true);
    }

    [TestMethod]
    public void CatalogRejectsUnknownDefaultRoleKeys()
    {
        IPermissionDefinitionProvider provider = new TestProvider([
            new("things", "Things", "Things module", 1,
                [new("things.read", "Read things", "Read things.", DefaultRoles: ["super-admin"])])
        ]);

        Should.Throw<InvalidOperationException>(() => new PermissionCatalog([provider]))
            .Message.ShouldContain("unknown default role");
    }

    [TestMethod]
    public async Task RoleManagerCannotGrantPermissionOutsideOwnBoundary()
    {
        PermissionCatalog catalog = CreateCatalog();
        TestOrganizationContext context = new([Permissions.RolesRead, Permissions.RolesManage]);
        OrganizationAdministration administration = new(
            null!,
            null!,
            null!,
            null!,
            context,
            new AllowPermissionAuthorizer(),
            catalog,
            null!);

        var result = await administration.SaveRoleAsync(
            new SaveRoleCommand(null, "Escalated", "Attempts privilege escalation.", [Permissions.OrganizationsManage]),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("forbidden");
    }

    private static PermissionCatalog CreateCatalog() => new([
        new BuiltInPermissionDefinitionProvider(),
        new ProjectPermissionDefinitionProvider()
    ]);

    private sealed class TestProvider(IReadOnlyList<PermissionModuleDefinition> modules) : IPermissionDefinitionProvider
    {
        public IReadOnlyList<PermissionModuleDefinition> GetModules() => modules;
    }

    private sealed class AllowPermissionAuthorizer : IPermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestOrganizationContext(IEnumerable<string> permissions) : IOrganizationContext
    {
        public bool IsResolved => true;
        public Guid OrganizationId { get; } = Guid.CreateVersion7();
        public string OrganizationSlug => "test";
        public Guid ActorId { get; } = Guid.CreateVersion7();
        public Guid MembershipId { get; } = Guid.CreateVersion7();
        public IReadOnlySet<string> Permissions { get; } = permissions.ToHashSet(StringComparer.Ordinal);
    }
}
