using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OperationalAccessProfileTests
{
    private static readonly string[] InboxPermissions = ["INSERT", "SELECT"];
    [TestMethod]
    public void OperationalOwnershipHasNoImplicitGrants()
    {
        DataResourceDescriptor work = new("module-work-items", "infrastructure", "module_work_items",
            ModuleDataOwnership.Infrastructure, AccessRule: ModuleDataAccessRule.HostOnly);
        Dictionary<string, DataResourceDescriptor> resources = new() { ["infrastructure.module_work_items"] = work };
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Outbox,
            "infrastructure.module_work_items", resources).ShouldBeEmpty();
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Organization,
            "infrastructure.module_work_items", new Dictionary<string, DataResourceDescriptor>()).ShouldBeEmpty();
        Enum.TryParse("WorkCoordinator", out RuntimeDatabaseRoleKind coordinator).ShouldBeTrue();
        RuntimeDatabaseAccessProfiles.PermissionsFor(coordinator, "app.projects", resources).ShouldBeEmpty();
        RuntimeDatabaseAccessProfiles.PermissionsFor(coordinator, "identity.AspNetUsers", resources).ShouldBeEmpty();
        RuntimeDatabaseAccessProfiles.PermissionsFor(coordinator, "infrastructure.unregistered", resources).ShouldBeEmpty();
    }

    [TestMethod]
    public void HostInboxIsAnExplicitAppendOnlyOrganizationException()
    {
        DataResourceDescriptor inbox = new("module-event-inbox", "app", "module_event_inbox",
            ModuleDataOwnership.Organization, IsolationPolicy: "module_event_inbox_read",
            AccessRule: ModuleDataAccessRule.HostOnly);
        Should.Throw<InvalidOperationException>(() => ModuleDataResourceRules.Validate(inbox));
        Should.NotThrow(() => ModuleDataResourceRules.Validate(inbox, hostOwned: true));
        Dictionary<string, DataResourceDescriptor> resources = new() { ["app.module_event_inbox"] = inbox };
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Organization,
            "app.module_event_inbox", resources).Order().ShouldBe(InboxPermissions);
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Outbox,
            "app.module_event_inbox", resources).ShouldBeEmpty();
    }
}
