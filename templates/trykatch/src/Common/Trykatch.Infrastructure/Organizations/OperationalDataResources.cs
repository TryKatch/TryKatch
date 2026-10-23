using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

/// <summary>Host-owned allocations, never installable module-provided privilege declarations.</summary>
internal static class OperationalDataResources
{
    public static IReadOnlyList<DataResourceDescriptor> All { get; } =
    [
        new("module-work-items", "infrastructure", "module_work_items", ModuleDataOwnership.Infrastructure,
            typeof(ModuleWorkItem).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("module-event-deliveries", "infrastructure", "module_event_deliveries", ModuleDataOwnership.Infrastructure,
            typeof(ModuleEventDelivery).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("module-event-inbox", "app", "module_event_inbox", ModuleDataOwnership.Organization,
            typeof(ModuleEventInbox).FullName, "module_event_inbox_read", ModuleDataAccessRule.HostOnly),
        new("module-work-completions", "app", "module_work_completions", ModuleDataOwnership.Organization,
            typeof(ModuleWorkCompletion).FullName, "module_work_completions_read", ModuleDataAccessRule.HostOnly)
    ];

    public static bool IsReserved(string relation) => All.Any(resource => $"{resource.Schema}.{resource.Table}" == relation);
}
