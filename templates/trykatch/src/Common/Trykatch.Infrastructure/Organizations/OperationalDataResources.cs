using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Realtime;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

/// <summary>Host-owned allocations, never installable module-provided privilege declarations.</summary>
internal static class OperationalDataResources
{
    public static IReadOnlyList<DataResourceDescriptor> All { get; } =
    [
        new("realtime-streams", "app", "realtime_streams", ModuleDataOwnership.Organization,
            typeof(RealtimeStream).FullName, "realtime_streams_read", ModuleDataAccessRule.HostOnly),
        new("realtime-history", "app", "realtime_history", ModuleDataOwnership.Organization,
            typeof(RealtimeHistoryRecord).FullName, "realtime_history_read", ModuleDataAccessRule.HostOnly),
        new("cache-invalidations", "app", "cache_invalidations", ModuleDataOwnership.Organization,
            typeof(CacheInvalidation).FullName, "cache_invalidations_read", ModuleDataAccessRule.HostOnly),
        new("organization-email-deliveries", "infrastructure", "email_deliveries", ModuleDataOwnership.Infrastructure,
            typeof(OrganizationEmailDeliveryRecord).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("identity-email-deliveries", "identity", "email_deliveries", ModuleDataOwnership.Platform,
            AccessRule: ModuleDataAccessRule.IdentityOnly),
        new("module-work-items", "infrastructure", "module_work_items", ModuleDataOwnership.Infrastructure,
            typeof(ModuleWorkItem).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("module-event-deliveries", "infrastructure", "module_event_deliveries", ModuleDataOwnership.Infrastructure,
            typeof(ModuleEventDelivery).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("module-event-inbox", "app", "module_event_inbox", ModuleDataOwnership.Organization,
            typeof(ModuleEventInbox).FullName, "module_event_inbox_read", ModuleDataAccessRule.HostOnly),
        new("module-schedule-cursors", "infrastructure", "module_schedule_cursors", ModuleDataOwnership.Infrastructure,
            typeof(ModuleScheduleCursor).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("module-work-controls", "infrastructure", "module_work_controls", ModuleDataOwnership.Infrastructure,
            typeof(ModuleWorkControl).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        .. QuartzSchemaContract.Tables.Select(table => new DataResourceDescriptor(
            "scheduler-" + table.Replace('_', '-'), "infrastructure", table, ModuleDataOwnership.Infrastructure,
            AccessRule: ModuleDataAccessRule.HostOnly)),
        new("module-work-completions", "app", "module_work_completions", ModuleDataOwnership.Organization,
            typeof(ModuleWorkCompletion).FullName, "module_work_completions_read", ModuleDataAccessRule.HostOnly)
    ];

    public static bool IsReserved(string relation) => All.Any(resource => $"{resource.Schema}.{resource.Table}" == relation);
}
