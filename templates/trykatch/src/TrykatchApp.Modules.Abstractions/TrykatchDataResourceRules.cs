namespace TrykatchApp.Modules;

/// <summary>Closed access profiles understood by the host, migrator and inspector.</summary>
public static class TrykatchDataResourceRules
{
    public static void Validate(TrykatchDataResourceDescriptor resource, bool hostOwned = false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        bool valid = resource.Ownership switch
        {
            TrykatchDataOwnership.Organization => resource.AccessRule is null
                && (resource.Schema == "app" || hostOwned && resource.Schema == "platform")
                && !string.IsNullOrWhiteSpace(resource.IsolationPolicy),
            TrykatchDataOwnership.Platform => resource.Schema == "platform"
                && resource.AccessRule == TrykatchDataAccessRule.PlatformOnly
                || resource.Schema == "identity" && resource.AccessRule == TrykatchDataAccessRule.IdentityOnly,
            TrykatchDataOwnership.Global => resource.Schema == "reference"
                && resource.AccessRule == TrykatchDataAccessRule.GlobalReadOnly,
            TrykatchDataOwnership.Infrastructure => resource.AccessRule == TrykatchDataAccessRule.HostOnly
                && (resource.Schema == "infrastructure" || hostOwned && resource.Schema == "platform")
                || hostOwned && resource.Schema == "platform" && resource.Table == "outbox_messages"
                    && resource.AccessRule == TrykatchDataAccessRule.OutboxAppendOnly,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException(
                $"Resource '{resource.Schema}.{resource.Table}' has no approved schema/ownership/access-rule combination.");
    }
}
