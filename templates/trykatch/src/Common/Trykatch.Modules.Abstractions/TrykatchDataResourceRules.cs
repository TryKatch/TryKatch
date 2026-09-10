namespace Trykatch.Modules;

/// <summary>Closed access profiles understood by the host, migrator and inspector.</summary>
public static class ModuleDataResourceRules
{
    public static void Validate(DataResourceDescriptor resource, bool hostOwned = false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        bool valid = resource.Ownership switch
        {
            ModuleDataOwnership.Organization => resource.AccessRule is null
                && (resource.Schema == "app" || hostOwned && resource.Schema == "platform")
                && !string.IsNullOrWhiteSpace(resource.IsolationPolicy),
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
