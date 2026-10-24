using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Idempotency;
using Trykatch.Infrastructure.Realtime;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

/// <summary>Host-owned allocations, never installable module-provided privilege declarations.</summary>
internal static class OperationalDataResources
{
    public static IReadOnlyList<DataResourceDescriptor> All { get; } =
    [
        new("feature-environment-controls", "platform", "feature_environment_controls", ModuleDataOwnership.Platform,
            AccessRule: ModuleDataAccessRule.PlatformOnly),
        new("organization-feature-overrides", "platform", "organization_feature_overrides", ModuleDataOwnership.Organization,
            IsolationPolicy: "organization_feature_overrides_read", AccessRule: ModuleDataAccessRule.HostOnly),
        new("feature-control-history", "platform", "feature_control_history", ModuleDataOwnership.Platform,
            AccessRule: ModuleDataAccessRule.PlatformOnly),
        new("entitlement-publication-history", "platform", "entitlement_publication_history", ModuleDataOwnership.Platform,
            IsolationPolicy: "entitlement_publication_history_platform_read", AccessRule: ModuleDataAccessRule.PlatformOnly),
        new("mutation-receipts", "app", "mutation_receipts", ModuleDataOwnership.Organization,
            typeof(MutationReceiptRecord).FullName, "mutation_receipts_read", ModuleDataAccessRule.HostOnly),
        new("realtime-streams", "app", "realtime_streams", ModuleDataOwnership.Organization,
            typeof(RealtimeStream).FullName, "realtime_streams_read", ModuleDataAccessRule.HostOnly),
        new("realtime-history", "app", "realtime_history", ModuleDataOwnership.Organization,
            typeof(RealtimeHistoryRecord).FullName, "realtime_history_read", ModuleDataAccessRule.HostOnly),
        new("cache-invalidations", "app", "cache_invalidations", ModuleDataOwnership.Organization,
            typeof(CacheInvalidation).FullName, "cache_invalidations_read", ModuleDataAccessRule.HostOnly),
        new("organization-entitlements", "app", "organization_entitlements", ModuleDataOwnership.Organization,
            typeof(OrganizationEntitlementRecord).FullName, "organization_entitlements_read", ModuleDataAccessRule.HostOnly),
        new("organization-quota-reservations", "app", "organization_quota_reservations", ModuleDataOwnership.Organization,
            typeof(OrganizationQuotaReservationRecord).FullName, "organization_quota_reservations_read", ModuleDataAccessRule.HostOnly),
        new("organization-quota-inventory", "app", "organization_quota_inventory", ModuleDataOwnership.Organization,
            typeof(OrganizationQuotaInventoryRecord).FullName, "organization_quota_inventory_read", ModuleDataAccessRule.HostOnly),
        new("organization-usage", "app", "organization_usage", ModuleDataOwnership.Organization,
            typeof(OrganizationUsageRecord).FullName, "organization_usage_read", ModuleDataAccessRule.HostOnly),
        new("module-upload-operations", "app", "module_upload_operations", ModuleDataOwnership.Organization,
            typeof(ModuleUploadOperationRecord).FullName, "module_upload_operations_read", ModuleDataAccessRule.HostOnly),
        new("quota-maintenance-work", "infrastructure", "quota_maintenance_work", ModuleDataOwnership.Infrastructure,
            typeof(QuotaMaintenanceWorkRecord).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("organization-email-deliveries", "infrastructure", "email_deliveries", ModuleDataOwnership.Infrastructure,
            typeof(OrganizationEmailDeliveryRecord).FullName, AccessRule: ModuleDataAccessRule.HostOnly),
        new("identity-account-sessions", "identity", "account_sessions", ModuleDataOwnership.Platform,
            AccessRule: ModuleDataAccessRule.IdentityOnly),
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
