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
    public void SchedulerStateHasOnlyExactCoordinatorGrants()
    {
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        foreach (string table in new[] { "module_schedule_cursors", "module_work_controls", "qrtz_job_details", "qrtz_triggers", "qrtz_locks" })
        {
            string relation = "infrastructure." + table;
            RuntimeDatabaseAccessProfiles.HostOwnedRelations.ShouldContain(relation);
            RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.WorkCoordinator, relation, resources)
                .ShouldContain("INSERT");
            foreach (RuntimeDatabaseRoleKind kind in Enum.GetValues<RuntimeDatabaseRoleKind>().Where(kind => kind != RuntimeDatabaseRoleKind.WorkCoordinator))
                RuntimeDatabaseAccessProfiles.PermissionsFor(kind, relation, resources).ShouldBeEmpty();
        }
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.WorkCoordinator,
            "infrastructure.module_work_controls", resources).Order().ShouldBe(["INSERT", "SELECT"]);
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.WorkCoordinator,
            "infrastructure.qrtz_execution_history", resources).ShouldBeEmpty();
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Organization,
            "infrastructure.module_work_items", resources).ShouldBe(["INSERT"]);
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

    [TestMethod]
    public void HostWorkCompletionsHaveOnlyOrganizationReadAndActorBoundInsert()
    {
        DataResourceDescriptor completions = new("module-work-completions", "app", "module_work_completions",
            ModuleDataOwnership.Organization, IsolationPolicy: "module_work_completions_read",
            AccessRule: ModuleDataAccessRule.HostOnly);
        Should.Throw<InvalidOperationException>(() => ModuleDataResourceRules.Validate(completions));
        Should.NotThrow(() => ModuleDataResourceRules.Validate(completions, hostOwned: true));

        Dictionary<string, DataResourceDescriptor> resources = new()
        {
            ["app.module_work_completions"] = completions
        };
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Organization,
            "app.module_work_completions", resources).Order().ShouldBe(["INSERT", "SELECT"]);
        foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>()
                     .Where(role => role != RuntimeDatabaseRoleKind.Organization))
        {
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, "app.module_work_completions", resources)
                .ShouldBeEmpty();
        }

        DataResourceDescriptor wrongPolicy = completions with { IsolationPolicy = "unexpected_policy" };
        RuntimeDatabaseAccessProfiles.PermissionsFor(RuntimeDatabaseRoleKind.Organization,
            "app.module_work_completions", new Dictionary<string, DataResourceDescriptor>
            {
                ["app.module_work_completions"] = wrongPolicy
            }).ShouldBeEmpty();
    }
}
