namespace Trykatch.Modules;

/// <summary>Closed access profiles understood by the host, migrator and inspector.</summary>
public static class ModuleDataResourceRules
{
    public static void Validate(DataResourceDescriptor resource, bool hostOwned = false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        bool valid = resource.Ownership switch
        {
            ModuleDataOwnership.Organization => !string.IsNullOrWhiteSpace(resource.IsolationPolicy)
                && (resource.AccessRule is null && (resource.Schema == "app" || hostOwned && resource.Schema == "platform")
                    || hostOwned && resource.Schema == "platform" && resource.Table == "organization_feature_overrides"
                        && resource.AccessRule == ModuleDataAccessRule.HostOnly
                    || hostOwned && resource.Schema == "app" && resource.Table is "module_event_inbox" or "module_work_completions" or "cache_invalidations" or "realtime_streams" or "realtime_history" or "mutation_receipts" or "organization_entitlements" or "organization_quota_reservations" or "organization_quota_inventory" or "organization_usage" or "module_upload_operations" or "organization_request_limits"
                        && resource.AccessRule == ModuleDataAccessRule.HostOnly),
            ModuleDataOwnership.Platform => resource.Schema == "platform"
                && resource.AccessRule == ModuleDataAccessRule.PlatformOnly
                || resource.Schema == "identity" && resource.AccessRule == ModuleDataAccessRule.IdentityOnly,
            ModuleDataOwnership.Global => resource.Schema == "reference"
                && resource.AccessRule == ModuleDataAccessRule.GlobalReadOnly,
            ModuleDataOwnership.Infrastructure => resource.AccessRule == ModuleDataAccessRule.HostOnly
                && (resource.Schema == "infrastructure" || hostOwned && resource.Schema == "platform")
                || hostOwned && resource.Schema == "platform" && resource.Table == "outbox_messages"
                    && resource.AccessRule == ModuleDataAccessRule.OutboxAppendOnly,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException(
                $"Resource '{resource.Schema}.{resource.Table}' has no approved schema/ownership/access-rule combination.");
    }
}
