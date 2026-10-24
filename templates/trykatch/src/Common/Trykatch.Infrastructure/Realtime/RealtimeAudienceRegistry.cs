using Trykatch.Modules;

namespace Trykatch.Infrastructure.Realtime;

internal sealed record RealtimeAudienceRegistration(
    ModuleRealtimeAudiencePolicyDescriptor Descriptor,
    IModuleRealtimeAudiencePolicy Adapter);

/// <summary>Scoped, explicit module adapters; this registry never supplies recipient authority.</summary>
internal sealed class RealtimeAudienceRegistry
{
    private readonly Dictionary<ModuleRealtimePolicyRef, RealtimeAudienceRegistration> policies;

    public RealtimeAudienceRegistry(ModuleCatalog catalog, IEnumerable<ModuleRealtimeAudiencePolicyBinding> bindings)
    {
        ModuleRealtimeAudiencePolicyBinding[] supplied = bindings.ToArray();
        ModuleRealtimeRegistrationRules.ValidateBindings(catalog.Descriptors, supplied);
        Dictionary<ModuleRealtimePolicyRef, IModuleRealtimeAudiencePolicy> adapters = supplied
            .ToDictionary(binding => binding.Policy, binding => binding.Adapter);
        policies = catalog.Descriptors.SelectMany(module => module.RealtimeAudiencePolicies)
            .ToDictionary(policy => new ModuleRealtimePolicyRef(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion),
                policy => new RealtimeAudienceRegistration(policy,
                    adapters[new(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion)]));
    }

    public IReadOnlyCollection<RealtimeAudienceRegistration> Registrations => policies.Values;

    public bool TryGet(ModuleRealtimePolicyRef policy, out RealtimeAudienceRegistration? registration) =>
        policies.TryGetValue(policy, out registration);

    public RealtimeAudienceRegistration RequireNotification(ModuleRealtimeNotification notification)
    {
        ModuleRealtimeRegistrationRules.ValidateNotification(notification);
        if (!policies.TryGetValue(notification.Audience.Policy, out RealtimeAudienceRegistration? registration)
            || registration.Descriptor.OwnerModuleId != notification.ModuleId
            || !registration.Descriptor.AllowedEvents.Contains(new(notification.EventKey, notification.SchemaVersion))
            || !registration.Descriptor.AllowedAudiences.Contains(notification.Audience.Kind)
            || notification.Audience.Resource is not null
                && !registration.Descriptor.AllowedResourceKeys.Contains(notification.Audience.Resource.ResourceKey, StringComparer.Ordinal))
            throw new InvalidOperationException("Realtime invalidation does not match an installed audience policy.");
        return registration;
    }
}
